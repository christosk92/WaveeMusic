// ── Themes/WinUi/Structure/Views/Artist/WinUiArtistHero.cs — WinUI's artist hero card (themes plan 6.2; P6 W6a) ───────────────────────────────────────
//
// Board W-Artist (and -Compact / -Small / -Huge / -Huge4K): a r8 CARD inside the page's inset, 200 / 220 / 260 / 420 tall (the tier record, `WinUiArtistRules`), not a
// full-bleed banner: the photograph under a 180-tall veil, the copy at the bottom-left (the Verified line, the name in Display 68, the meta "20,577,457 monthly
// listeners · #112 in the world") and, right-aligned on the same baseline, the actions: Play (accent), Follow, Shuffle, Artist radio and "..." on a 70 % dark
// plate. Shuffle and radio are glyphs alone below Huge; at Compact the actions are a row UNDER the card at the 40 targets. The hero scrolls away with the page (the
// sticky piece of this page is the pivot, `WinUiArtistPage`), so there is no collapse arithmetic and no band.
//
// The page view builds the card for the live page AND for the derived shimmer (the skeleton deriver cannot run a view slot): a null `play` is the skeleton arm, whose
// text runs are bars at their line boxes and whose actions are shapes.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's artist hero card.</summary>
internal static class WinUiArtistHero
{
    static readonly Action s_noop = static () => { };

    /// <summary>The hero's verbs (all null on the skeleton arm).</summary>
    internal readonly record struct Acts(Action? Play, Action? Shuffle, Action? Radio, Func<ContextMenuModel?>? Menu);

    /// <summary>The card for a <paramref name="cardW"/>-wide band in window tier <paramref name="tier"/>: the photograph (flat neutral without one), the veil, the copy
    /// and, at every tier but Compact, the actions inside it. At Compact the actions are <see cref="CompactActionsSlot"/>, the page's row under the card.</summary>
    public static Element Card(in Artist.HeroText text, string uri, string? photoUrl, float cardW, WidthTier tier, in Acts acts, WinUiArtistLatch? latch = null)
    {
        var form = WinUiArtistRules.HeroOf(tier);
        float height = form.Height;
        bool compact = tier == WidthTier.Compact;
        bool shimmer = acts.Play is null;
        float w = MathF.Max(1f, cardW);

        // A photograph with faces places them in the band above the copy (lane B10); the photograph waits for its subject (it always settles), so its
        // crop never changes once it shows. Without faces the centred crop stands.
        PhotoSubject? subject = shimmer || photoUrl is not { Length: > 0 } measured ? null : Palette.Subject(measured);
        var crop = subject is { Faces: true } faces
            ? new MediaScrim.Crop(faces.X, faces.Y, WinUiArtistRules.PhotoFacesAtX, CopyTop(in text, form) * WinUiArtistRules.PhotoFacesAboveCopy / height)
            : default;
        // The layers stretch with the card (no width or height of their own: the card's height eases across a crossing, its width follows the page's
        // easing inset), so the photograph fills it and the copy rides its foot frame by frame.
        Element photo = photoUrl is { Length: > 0 } src && (shimmer || subject is not null)
            ? Controls.Artwork(src, w, height, WinUiArtistRules.HeroRadius, decodePx: WinUiArtistRules.PhotoDecode, crop: crop).As<BoxEl>() with
            {
                Key = "heroart:" + uri, Width = float.NaN, Height = float.NaN,
            }
            // The skeleton arm states the card's size: the deriver makes a plate only of a SIZED childless box, so the stretching placeholder derived to
            // nothing and the loading page drew the copy's bars alone, a small block where the 260-tall card lands (owner video 2026-10-25).
            : shimmer
                ? new BoxEl { Width = w, Height = height, Corners = CornerRadius4.All(WinUiArtistRules.HeroRadius) }
                : new BoxEl { Fill = Design.WatchedPlaceholder(photoUrl) };
        Element veil = new BoxEl
        {
            Height = form.Veil, AlignSelf = FlexAlign.End, HitTestVisible = false, Layout = WinUiArtistMotion.HeroHeight,
            Gradient = GradientDown(new GradientStop(0f, WinUiArtistRules.VeilClear), new GradientStop(1f, WinUiArtistRules.VeilEdge)),
        };

        // Over the photograph the copy is on-media ink in light AND dark; on a bright photograph the veil is not enough, so the adaptive boost darkens it
        // until every line reads (MediaScrim: the meta and the Verified line at 4.5 : 1, the name at 3 : 1, against the photograph's p90 under each line).
        Element boost = shimmer || photoUrl is not { Length: > 0 } boostSrc
            ? new BoxEl { HitTestVisible = false }
            : Controls.ScrimBoost(BoostOf(in text, boostSrc, form, w, crop));
        Element copy = Copy(in text, uri, form, shimmer);
        var bottom = new List<Element>(2) { new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [copy] } };
        if (!compact)
            bottom.Add(new BoxEl
            {
                Key = "artist-hero-actions:card", Shrink = 0f, Margin = new Edges4(0f, 0f, 0f, WinUiArtistRules.ActionsBottom),
                // Leaving (into Compact) it closes its width as it fades (a wipe from its end), so less of it lies over the copy, which the engine
                // widens at once (a leaving flex sibling gives its room back on the commit).
                Layout = latch is { HeroLive: true } ? WinUiArtistMotion.CardActions : WinUiArtistMotion.CardActionsLanding,
                Children = [Actions(text.Name, uri, acts, WinUiDetailRules.Target, words: tier >= WidthTier.Huge, plated: true)],
            });
        Element overlay = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.End, Justify = FlexJustify.Start, Gap = WinUiArtistRules.ActionsGap,
            Padding = new Edges4(form.InsetX, 0f, form.InsetX, form.Bottom),
            Children = bottom.ToArray(),
        };
        return new BoxEl
        {
            Key = "artist-hero", Height = height, ZStack = true, ClipToBounds = true, Animate = WinUiArtistMotion.HeroHeight, OnRealized = latch?.MarkHeroLive,
            Corners = CornerRadius4.All(WinUiArtistRules.HeroRadius), Fill = Prop.Of(static () => Tok.FillCardDefault),
            Children = [photo, veil, boost, overlay],
        };
    }

    /// <summary>The boost's reading of this card: the copy's line bands from the card's foot up (the meta, the name in its natural line box, the Verified
    /// line), the 62 % veil as the floor, the ceiling.</summary>
    static Controls.ScrimBoostProps BoostOf(in Artist.HeroText text, string photoUrl, WinUiArtistHeroForm form, float w, MediaScrim.Crop crop)
    {
        float height = form.Height;
        float left = form.InsetX, right = form.InsetX + MathF.Min(WinUiArtistTiers.CopyMax, MathF.Max(1f, w - 2f * form.InsetX));
        float metaTop = height - form.Bottom - form.MetaLine;
        float nameBox = MathF.Max(form.NameLine, form.NameSize * WinUiArtistRules.NameNaturalLine);
        float nameTop = metaTop - WinUiArtistRules.CopyGap - nameBox;
        float verifiedTop = nameTop - WinUiArtistRules.CopyGap - WinUiArtistRules.VerifiedLine;
        var bands = new List<MediaScrim.Band>(3)
        {
            MediaScrim.Band.Line(left, right, metaTop, form.MetaLine, form.MetaSize),
            MediaScrim.Band.Line(left, right, nameTop, nameBox, form.NameSize, 600),
        };
        if (text.Verified) bands.Add(MediaScrim.Band.Line(left, right, verifiedTop, WinUiArtistRules.VerifiedLine, WinUiArtistRules.VerifiedSize));
        var floor = new MediaScrim.Floor(height - form.Veil, form.Veil, WinUiArtistRules.VeilEdge.A);
        return new Controls.ScrimBoostProps(photoUrl, w, height, bands.ToArray(), floor, WinUiArtistRules.BoostCeiling, text.Verified ? verifiedTop : nameTop, crop);
    }

    /// <summary>Where the card's copy starts (its Verified line, else the name), from the card's top: the band above it is the photograph's.</summary>
    static float CopyTop(in Artist.HeroText text, WinUiArtistHeroForm form)
    {
        float metaTop = form.Height - form.Bottom - form.MetaLine;
        float nameTop = metaTop - WinUiArtistRules.CopyGap - MathF.Max(form.NameLine, form.NameSize * WinUiArtistRules.NameNaturalLine);
        return text.Verified ? nameTop - WinUiArtistRules.CopyGap - WinUiArtistRules.VerifiedLine : nameTop;
    }

    /// <summary>The Compact page's actions row under the card: Play, Follow, Shuffle, radio, then "..." at the row's end, every target 40. It stands in a slot
    /// that is in the head at every tier and eases its height (empty above Compact), and it cross-fades with the card's plated actions
    /// (<see cref="WinUiArtistMotion.ActionsSwap"/>).</summary>
    public static Element CompactActionsSlot(string name, string uri, in Acts acts, bool compact) => new BoxEl
    {
        Key = "artist-actions:slot", Direction = 1, MinWidth = 0f, Layout = WinUiArtistMotion.HeroHeight,
        Children = compact
            ? [new BoxEl
            {
                Key = "artist-actions", Direction = 0, MinWidth = 0f,
                Margin = new Edges4(0f, WinUiArtistRules.ActionsRowTop, 0f, 0f),
                Enter = WinUiArtistMotion.ActionsSwap, Exit = WinUiArtistMotion.ActionsSwap, Transition = WinUiArtistMotion.ActionsSwapMotion,
                Children = [Actions(name, uri, acts, WinUiDetailRules.CompactTarget, words: false, plated: false)],
            }.Skeletonized(false)]
            : [],
    }.Skeletonized(false);

    // ── the copy ──

    static Element Copy(in Artist.HeroText text, string uri, WinUiArtistHeroForm form, bool shimmer)
    {
        Element verified = text.Verified
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiArtistRules.SealGap,
                Children =
                [
                    Seal(),
                    Lines(new TextEl(Loc.Get(Strings.Artist.Verified))
                    {
                        Size = WinUiArtistRules.VerifiedSize, LineHeight = WinUiArtistRules.VerifiedLine, Color = R.OnMedia.Ink,
                    }, shimmer),
                ],
            }
            : new BoxEl();
        TextEl name = R.Type.ArtistDisplay(text.Name) with
        {
            // One line first: the name shrinks toward its floor to stay on one line, and a name the floor cannot hold wraps by whole words onto a second
            // line instead of ending in an ellipsis beside the plated actions ("Kris Kross Amst…", owner video 2026-10-25).
            Size = form.NameSize, LineHeight = form.NameLine, MinSize = MathF.Min(WinUiArtistRules.NameFloor, form.NameSize), Weight = 600,
            Color = R.OnMedia.Ink, Wrap = TextWrap.WrapWholeWords, MaxLines = WinUiArtistRules.NameLines, FitMode = TextFitMode.OneLineFirst,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Key = "artist-hero-copy", Direction = 1, Gap = WinUiArtistRules.CopyGap, MinWidth = 0f, MaxWidth = WinUiArtistTiers.CopyMax,
            Children = [verified, Lines(name, shimmer), Meta(in text, form, shimmer)],
        };
    }

    /// <summary>The accent seal with its check (the board: a 14 round accent with a black check).</summary>
    static Element Seal() => new BoxEl
    {
        Width = WinUiArtistRules.SealSize, Height = WinUiArtistRules.SealSize, Shrink = 0f, Corners = CornerRadius4.All(WinUiArtistRules.SealSize / 2f),
        Fill = Prop.Of(static () => Tok.AccentDefault), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [Icon(Icons.Accept, WinUiArtistRules.SealGlyph, ColorContrast.PickContrast(Tok.AccentDefault))],
    };

    /// <summary>"20,577,457 monthly listeners · #112 in the world": Body 14 in the on-media ink (the board's Text 1 is white over the photograph); a zero stat drops its run.</summary>
    static Element Meta(in Artist.HeroText text, WinUiArtistHeroForm form, bool shimmer)
    {
        var parts = new List<string>(3);
        if (text.Monthly > 0)
            parts.Add(Strings.Theme.Winui.Detail.MonthlyListeners(count: text.Monthly.ToString("N0", CultureInfo.CurrentCulture)));
        if (text.Rank > 0 && form.RankInMeta) parts.Add(Strings.Artist.WorldRank(text.Rank.ToString(CultureInfo.CurrentCulture)));
        if (text.Monthly == 0 && text.Followers > 0)
            parts.Add(text.Followers.ToString("N0", CultureInfo.CurrentCulture) + " " + Loc.Get(Strings.Artist.MetaFollowers).ToLowerInvariant());
        if (parts.Count == 0) return new BoxEl();
        return Lines(new TextEl(string.Join(WinUiDetailRules.MetaLead, parts))
        {
            Size = form.MetaSize, LineHeight = form.MetaLine, Color = R.OnMedia.Ink,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        }, shimmer);
    }

    /// <summary>The skeleton arm states a run as its line box (the deriver paints a bar per text leaf at 0.72 of its size, which came out short of the loaded copy).</summary>
    static Element Lines(Element run, bool shimmer)
    {
        if (!shimmer || !run.Is(out TextEl t)) return run;
        return new BoxEl { Height = float.IsNaN(t.LineHeight) ? t.Size : t.LineHeight, MinWidth = 0f, Width = WinUiArtistRules.ShimmerLineW, Corners = CornerRadius4.All(WinUiDetailRules.BoneRadius) };
    }

    // ── the actions ──

    /// <summary>Play · Follow · Shuffle · Artist radio · "...". <paramref name="plated"/>: each non-accent action rides the 70 % dark plate with its hairline (over the photograph).
    /// <paramref name="words"/>: Shuffle and radio carry their words (Huge). Never skeleton content: the row stands as shapes only through its Play.</summary>
    static Element Actions(string name, string uri, in Acts acts, float target, bool words, bool plated)
    {
        var size = target > WinUiDetailRules.Target ? ControlSize.Large : ControlSize.Medium;
        Element play = Controls.PlayButton(Tok.AccentDefault, acts.Play ?? s_noop, size, Loc.Get(Strings.Artist.Play));
        // Follow is WinUI's neutral toggle (W-Artist: "Follow" on the same dark plate as Shuffle and radio over the photograph).
        Element follow = uri.Length == 0
            ? WinUiDetailKit.Follow("", name, size, plated)
            : WinUiDetailKit.Follow(uri, name, size, plated) with { Key = "artist-follow:" + uri };
        Element shuffle = Control(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), acts.Shuffle, target, words, plated);
        Element radio = Control(Icons.RadioTower, Loc.Get(Strings.Theme.Winui.Detail.ArtistRadio), acts.Radio, target, words, plated);
        var kids = new List<Element>(6) { play, follow, shuffle, radio };
        if (acts.Menu is { } menu) kids.Add(WinUiDetailKit.MoreButton(menu, target, plated ? WinUiDetailKit.MoreLook.Plated : WinUiDetailKit.MoreLook.Bordered) with { Key = "artist-more:" + uri });
        else kids.Add(new BoxEl { Width = target, Height = target, Shrink = 0f });
        if (!plated) kids.Insert(kids.Count - 1, new BoxEl { Key = "artist-actions:spring", Grow = 1f, MinWidth = 0f });
        return new BoxEl
        {
            Key = "artist-hero-actions", Direction = 0, Gap = WinUiArtistRules.ActionsGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Grow = plated ? 0f : 1f, Children = kids.ToArray(),
        }.Skeletonized(false);
    }

    /// <summary>A secondary action: the glyph and its word (Huge) or the glyph alone, on the control fill (plated: on-media, the 70 % dark plate and the
    /// on-media ink over the photograph in light and dark, hover and press down the on-media scrim ramp).</summary>
    static Element Control(string glyph, string label, Action? onClick, float target, bool word, bool plated)
    {
        Action click = onClick ?? s_noop;
        BoxEl box = (word, plated) switch
        {
            (true, true) => Button.Create(label, click, ButtonAppearance.Standard, glyph: glyph,
                palette: Controls.OnMediaPlatePalette(WinUiArtistRules.PlateFill, WinUiArtistRules.PlateStroke)) with { Height = target, MinWidth = 0f, Shrink = 0f },
            (true, false) => Button.Create(label, click, ButtonAppearance.Standard, glyph: glyph) with { Height = target, MinWidth = 0f, Shrink = 0f },
            (false, true) => Controls.OnMediaIconAction(glyph, click, size: target, rest: WinUiArtistRules.PlateFill, stroke: WinUiArtistRules.PlateStroke),
            _ => Controls.IconAction(glyph, click, size: target) with
            {
                Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
            },
        };
        return Controls.Named(box, label);
    }
}
