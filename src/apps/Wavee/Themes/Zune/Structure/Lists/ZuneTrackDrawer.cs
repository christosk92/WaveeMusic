// ── Themes/Zune/Structure/Lists/ZuneTrackDrawer.cs — Zune's expanded-row drawer (themes plan 6.3; P5 Z3) ──────────────────────────
//
// No Zune board draws the drawer, so it is built from Zune's parts (owner Q18): flat and square under its row (no zebra, no corners, no inset:
// Zune's rows are full-width plates); the facts strip in Zune's type (the hero figures in the StatHero role over lowercased captions, the
// middot prose line and the genre / flag line in the TrackMeta role); the "versions and formats" heading lowercased; the connected version
// rows with the self row on the 5 % wash, covers at the Cover radius, the playing version's title in the accent; the format split button's
// two square halves washing on hover.
//
// PER TIER (ZT5). The version row is a flex row (the cover, the title over its meta, the waveform strip, the format split button); the waveform is the
// one part with a fixed width (64 bars, 191 DIP), so it leaves the row where the CONTENT tier is Compact (a 300 page has 190 DIP for the whole row)
// and fades back in from Small. It is its own component (`WaveHost`, a template here has no view context), reading the tier it is drawn in.
//
// WHAT THE CORE KEEPS. Which drawer is open, the keyed mount, the reveal wiring (`OnRealized`), the indent, the demand and the relations, the
// facts, the version entries and their order, the reserved video row, the waveform's peaks, the "arriving" latch, the format override and
// its overlay, and the play verb. A template draws and never names core state (WVTH002).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

using DrawerRules = global::Wavee.Track.DrawerRules;
using Fact = global::Wavee.Track.Fact;
using FactForm = global::Wavee.Track.FactForm;
using FactKind = global::Wavee.Track.FactKind;
using Facts = global::Wavee.Track.Facts;
using FactSplit = global::Wavee.Track.FactSplit;
using Format = global::Wavee.Track.Format;

/// <summary>Zune's style for the track drawer.</summary>
internal static class ZuneTrackDrawer
{
    /// <summary>The style Zune registers under <c>StyleKeys.TrackDrawer</c>. Stateless.</summary>
    public static readonly TrackDrawerStyle Style = new(Frame, Body, FormatSplit);

    // ══ THE FRAME ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One drawer at a time, keyed by row identity; the clip box carries the drawer's reveal (the presented height springs, the rows
    /// below ride it); the presence box carries the indent. Flat: no zebra, no corners.</summary>
    static Element Frame(TrackDrawerFrameArgs a)
    {
        string rowKey = a.RowKey;
        return new BoxEl
        {
            Key = "drawer:" + rowKey, Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = Design.Reveal.Drawer,
            OnRealized = a.OnRealized,
            Children =
            [
                new BoxEl
                {
                    Key = "drawer-presence:" + rowKey, Direction = 1, MinWidth = 0f,
                    Padding = new Edges4(a.Indent, 0f, Spacing.L, Spacing.S),
                    Children = [a.Body with { Key = "drawer-body:" + rowKey }],
                },
            ],
        };
    }

    // ══ THE BODY ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Body(TrackDrawerBodyArgs a)
    {
        var entries = a.Entries;
        var body = new Element[2 + entries.Count];
        body[0] = FactsStrip(a.Facts, a.Arriving);
        body[1] = R.Type.Eyebrow(Loc.Get(Strings.Detail.Versions.VersionsAndFormats)) with
        {
            Key = "versions-head", Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Heading),
            Margin = new Edges4(0f, Spacing.M, 0f, ZuneListRules.DrawerHeadBottom),
        };
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            Element row = e.Reserved ? PendingVersionRow() : VersionRow(a, e.Version, e.Kind, e.RowKey, e.IsNow, e.Peaks);
            body[2 + i] = ConnectedRow(e.RowKey, row, isLast: i == entries.Count - 1);
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = body };
    }

    // ══ THE FORMAT SPLIT BUTTON ══════════════════════════════════════════════════════════════════════════════════════════

    static Element FormatSplit(TrackDrawerFormatArgs f) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Stretch, Shrink = 0f, OnRealized = f.OnRealized,
        Children =
        [
            Half(Icons.Play, ZuneListRules.PlayGlyph, Tok.TextPrimary, ZuneListRules.PlayHalfWidth, f.Play),
            Half(Icons.ChevronDown, ZuneListRules.CaretGlyph2, Tok.TextSecondary, ZuneListRules.CaretHalfWidth, f.Open),
        ],
    };

    static BoxEl Half(string glyph, float glyphSize, ColorF ink, float width, Action onClick) => new()
    {
        Width = width, Height = ZuneListRules.SplitHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = Prop.Of(static () => R.Layer.RowHover), HoverFill = Prop.Of(static () => R.Layer.RowPressed),
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true, OnClick = onClick,
        Children = [Icon(glyph, glyphSize, ink)],
    };

    // ══ THE FACTS STRIP, THE VERSIONS ════════════════════════════════════════════════════════════════════════════════════

    static float StatGap => Spacing.XL;

    static readonly EnterExit? s_fadeUp = new EnterExit(Opacity: 0f, Active: true);

    static readonly string[] s_factKeys =
    [
        "fact:plays", "fact:bpm", "fact:key", "fact:added", "fact:duration", "fact:album", "fact:released",
        "fact:addedby", "fact:isrc", "fact:descriptors", "fact:explicit", "fact:video", "fact:local", "fact:unavailable",
    ];

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
                for (int k = 0; k < tags.Count; k++) Join(genres, Meta(tags[k], Tok.TextTertiary));
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
                Stagger = Design.Reduced || !arriving ? 0f : R.Motion.MastheadStaggerMs,
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

    static BoxEl FactLine(string key, List<Element> runs, bool arriving) => new()
    {
        Key = key, Enter = arriving ? s_fadeUp : null, Layout = ZuneMotion.PivotSlide, Direction = 0, Wrap = true, AlignItems = FlexAlign.Center,
        Gap = Spacing.XS, MinWidth = 0f, Children = runs.ToArray(),
    };

    static Element Stat(in Fact f, bool arriving)
    {
        bool pending = f.Form == FactForm.Pending;
        var split = pending ? new FactSplit(f.Value, null) : Facts.HeroSplit(in f);
        return new BoxEl
        {
            Key = s_factKeys[(int)f.Kind], Enter = arriving ? s_fadeUp : null, Layout = ZuneMotion.PivotSlide, Direction = 1, Shrink = 0f,
            MinWidth = 0f, Margin = new Edges4(0f, 0f, StatGap, 0f), Opacity = pending ? ZuneListRules.PendingFactOpacity : 1f,
            Children =
            [
                R.Type.StatHero(split.Value, split.Unit),
                Meta(Loc.Get(Facts.LabelKey(f.Kind)), Tok.TextTertiary) with { Case = R.Case.For(LabelKind.Label) },
            ],
        };
    }

    static TextEl Meta(string text, ColorF ink) => R.Type.TrackMeta(text) with
    {
        Color = ink, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
    };

    static SpanTextEl MetaSpans(TextSpan[] spans)
    {
        var meta = R.Type.TrackMeta("");
        return new SpanTextEl(spans)
        {
            Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary,
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
        };
    }

    static Element ProseRun(in Fact f)
    {
        if (f.Form == FactForm.Link)
        {
            var uri = f.LinkUri;
            string name = f.Value;
            Action? open = uri.IsValid ? () => Track.OpenEntity(uri, name) : null;
            return MetaSpans([new TextSpan(name, OnClick: open)]);
        }
        if (f.Kind == FactKind.AddedBy && f.Person.IsValid) return AddedBy(f.Person, f.Value);
        if (!DrawerRules.NeedsLabel(f.Kind)) return Meta(f.Value, Tok.TextSecondary);
        // Label and value as ONE paragraph, so a wrap never strands the label above its value.
        return MetaSpans([new TextSpan(Loc.Get(Facts.LabelKey(f.Kind)) + " ", Color: Tok.TextTertiary), new TextSpan(f.Value, Color: Tok.TextSecondary)]);
    }

    static Element AddedBy(User person, string name) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XXS, Shrink = 0f, MinWidth = 0f,
        Children =
        [
            Meta(Loc.Get(Facts.LabelKey(FactKind.AddedBy)) + " ", Tok.TextTertiary) with { Shrink = 0f },
            PersonPicture.Create("", Spacing.L, displayName: name, imageSourcePath: Controls.ArtUrl(person.ImageId)),
            Meta(name, Tok.TextSecondary),
        ],
    };

    static Element FlagRun(FactKind kind)
    {
        var label = Meta(Loc.Get(Facts.LabelKey(kind)), Tok.TextTertiary);
        string? glyph = kind switch
        {
            FactKind.Video => Icons.Movie, FactKind.LocalFile => Icons.Folder, FactKind.Unavailable => Icons.Info, _ => null,
        };
        if (glyph is null) return label;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XXS, Shrink = 0f, MinWidth = 0f,
            Children = [Icon(glyph, ZuneListRules.StarGlyph, Tok.TextTertiary), label],
        };
    }

    static void Join(List<Element> line, Element run)
    {
        if (line.Count > 0) line.Add(Meta("·", Tok.TextTertiary) with { Shrink = 0f });
        line.Add(run);
    }

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
                        Margin = new Edges4(DrawerRules.RailX, DrawerRules.RowH / 2f, 0f, 0f), Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
                    },
                ],
            },
            body,
        ],
    };

    /// <summary>The reserved music-video row: the real row's geometry and key, placeholder blocks, inert.</summary>
    static Element PendingVersionRow() => new BoxEl
    {
        Key = "ver:" + DrawerRules.VideoRowKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
        Grow = 1f, Height = DrawerRules.RowH, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), HitTestPassThrough = true,
        Children =
        [
            new BoxEl { Width = DrawerRules.VideoThumbW, Height = DrawerRules.VideoThumbH, Shrink = 0f, Fill = Prop.Of(static () => R.Layer.RowHover) },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ZuneListRules.DrawerPendingGap,
                Children = [Controls.PendingBar(ZuneListRules.PendingTitleBarW, ZuneListRules.PendingTitleBarH),
                            Controls.PendingBar(ZuneListRules.PendingMetaBarW, ZuneListRules.PendingMetaBarH)],
            },
        ],
    };

    static Element VersionRow(TrackDrawerBodyArgs a, Track version, byte kind, string rowKey, bool isNow, float[]? peaks)
    {
        Track parent = a.Track;
        var meta = new List<Element>(6) { Meta(KindLabel(kind), Tok.TextTertiary) with { Case = R.Case.For(LabelKind.Label) } };
        if (version.Knows(TrackFields.Audio) && version.Tempo > 0)
        {
            if (version.CamelotColor != 0)
                meta.Add(new BoxEl
                {
                    Width = ZuneListRules.TempoDot, Height = ZuneListRules.TempoDot, Opacity = ZuneListRules.TempoDotOpacity, Shrink = 0f,
                    AlignSelf = FlexAlign.Center, Fill = R.Ink.DataDot(version.CamelotColor),
                });
            meta.Add(Meta(Format.TempoLabel(version.Tempo), Tok.TextSecondary));
            if (Facts.KeyLabel(Format.CamelotLabel(version.Camelot), Format.KeyLabel(version.Key)) is { Length: > 0 } key)
            {
                meta.Add(Meta("·", Tok.TextTertiary));
                meta.Add(Meta(key, Tok.TextTertiary));
            }
        }
        if (version.DurationMs > 0) meta.Add(Meta(Format.DurationCell(version.DurationMs), Tok.TextTertiary));

        return new BoxEl
        {
            Key = "ver:" + rowKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Grow = 1f,
            Height = DrawerRules.RowH, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
            Fill = kind == DrawerRules.SelfRow ? Prop.Of(static () => R.Layer.RowHover) : (Prop<ColorF>)ColorF.Transparent,
            HoverFill = Prop.Of(static () => R.Layer.RowPressed),
            Children =
            [
                Thumb(version, parent, kind, isNow, a.PlayVersion),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ZuneListRules.DrawerTextGap,
                    Children =
                    [
                        R.Type.TrackTitle(version.Knows(TrackFields.Title) ? version.Title : "") with
                        {
                            Color = ZuneListRules.RowInk(isNow, R.Ink.AccentAction, Tok.TextPrimary),
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = meta.ToArray() },
                    ],
                },
                Embed.Comp(new WaveProps(peaks), static () => new WaveHost()) with { Key = "waveform-slot" },
                a.FormatButton(version, parent, kind),
            ],
        };
    }

    sealed record WaveProps(float[]? Peaks);

    /// <summary>The waveform slot: the strip where the peaks are known and the content tier has the room, else nothing. The strip enters and leaves
    /// with the arms' cross-fade (the slot is an unkeyed box over a children list, so the keyed strip's Enter / Exit run).</summary>
    sealed class WaveHost : Component, IPropsHost
    {
        WaveProps? _latest;
        readonly Signal<WaveProps?> _props = new(null);

        public void ApplyProps(object props)
        {
            _latest = (WaveProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            bool room = tiers.Policy is null || tiers.Content >= WidthTier.Small;
            var peaks = _latest?.Peaks;
            if (peaks is null || !room) return new BoxEl { Shrink = 0f };
            return new BoxEl
            {
                Shrink = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Key = "waveform", Shrink = 0f, Enter = ZuneDetailRules.ArmFade, Exit = ZuneDetailRules.ArmFade, Transition = ZuneDetailRules.ArmFadeMotion,
                        Children = [Waveform.Create(new WaveformModel(peaks, DrawerRules.WaveBars), key: "waveform-strip")],
                    },
                ],
            };
        }
    }

    /// <summary>16:9 with a play badge for the video, the bare square otherwise, at the Cover radius; the now-playing overlay on the version
    /// that IS the deck's item.</summary>
    static Element Thumb(Track version, Track parent, byte kind, bool isNow, Action<Track, Track, byte> playVersion)
    {
        bool video = kind == DrawerRules.VideoKind;
        float w = video ? DrawerRules.VideoThumbW : DrawerRules.AudioThumb;
        float h = video ? DrawerRules.VideoThumbH : DrawerRules.AudioThumb;
        string? url = Controls.ArtUrl(video && !parent.VideoImageId.IsEmpty ? parent.VideoImageId : version.ImageId);
        Element art = Controls.Artwork(url, w, h, R.Shape.Cover);
        if (!isNow && !video) return art;
        Element layer = isNow
            ? Controls.NowPlayingOverlay(version.Uri.Text, () => playVersion(version, parent, kind), fab: ZuneListRules.VersionFab(w, h), centred: true).Skeletonized(false)
            : new BoxEl
            {
                Width = w, Height = h, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestPassThrough = true,
                Fill = Prop.Of(static () => R.OnMedia.CoverScrim),
                Children =
                [
                    new BoxEl
                    {
                        Width = ZuneListRules.PlayBadge, Height = ZuneListRules.PlayBadge, Corners = Radii.Circle(ZuneListRules.PlayBadge), Shrink = 0f,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Fill = Prop.Of(static () => R.Ink.AccentAction),
                        Children = [Icon(ZunePlayerLayout.PlaySolid, ZuneListRules.PlayBadgeGlyph, Tok.TextOnAccentPrimary)],
                    },
                ],
            };
        return new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, ZStack = true, Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true,
            Children = [art, layer],
        };
    }

    static string KindLabel(byte kind) => kind switch
    {
        DrawerRules.VideoKind => Loc.Get(Strings.Detail.Versions.MusicVideo),
        DrawerRules.AudioKind => Loc.Get(Strings.Detail.Versions.AlternateAudio),
        _ => Loc.Get(Strings.Detail.Versions.ThisTrack),
    };
}
