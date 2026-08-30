using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// Semantic type aliases so call sites read INTENT, not raw sizes. Every alias maps to the engine's WinUI type ramp
// (Ui.Caption/Body/BodyStrong/Subtitle/Title/Display in Dsl/Typography.cs). Never author a raw `TextEl { Size = … }`.
//
// THE THREE-PART CONTRACT. Every alias below resolves a SIZE, a LINE HEIGHT and a WEIGHT — never a size alone. That is
// the whole reason to go through an alias rather than `with { Size = 13f }`: a bare size override keeps whatever line
// height the previous rung published, and a page of hand-picked sizes therefore has no vertical rhythm at all. The
// engine ramp carries the pair (12/16 · 14/20 · 14/20-600 · 18/24 · 20/28-600 · 28/36-600 · 40/52-600 · 68/92-600), so
// repointing a call site at an alias brings the line height with it.
//
// WEIGHT POLICY: 400 and 600 only, with two documented divergences. The three DISPLAY-FACE identity aliases
// (ArtistDisplay / ArtistTitle / ArtistCompactTitle) keep their documented 700 — the masthead voice, not UI labels.
// PivotLabel and NpvLyric keep SemiLight 350 — the same WinUI SemiLight the engine's own Pivot control
// (FluentGpu.Controls/Pivot.cs) already uses for its header row; a pivot / Zune lyric reel is a control cut, not a
// body-text weight, so it is not bound by the policy.
public static class WaveeType
{
    /// <summary>Track / album / playlist titles in lists. → Ui.BodyStrong (14 / 20 / 600).</summary>
    public static TextEl TrackTitle(string s) => Ui.BodyStrong(s);

    /// <summary>A media CARD's headline (a playlist tile, a mix card, a feed card, a shelf cell) — the same rung as
    /// <see cref="TrackTitle"/>, named for the surface it actually sits on so a card body does not have to claim it is
    /// rendering a track. → Ui.BodyStrong (14 / 20 / 600).</summary>
    public static TextEl CardTitle(string s) => Ui.BodyStrong(s);

    /// <summary>Artist · duration · metadata. → Ui.Caption secondary (12 / 16 / 400).</summary>
    public static TextEl TrackMeta(string s) => Ui.Caption(s).Secondary();

    /// <summary>THE tracking every eyebrow carries — 30/1000 em, owned by <see cref="Eyebrow"/> and authored nowhere
    /// else. Before convergence the app carried NINE tracking values on this one role (10, 20, 30, 32, 40, 50, 60, 70,
    /// 80, 120): a letterspacing ladder nobody designed, spread over 58 call sites, which is why two eyebrows stacked
    /// on the same page never looked like the same label. 30 is the value that survives SENTENCE case — the old 60-120
    /// rungs were compensating for ALL-CAPS, and caps is exactly what this role gave up.</summary>
    public const float EyebrowTracking = 30f;

    /// <summary>An EYEBROW — the small label that names what a card/section IS ("Editorial", "Daily Mix", "Video",
    /// "Release", the hero's greeting). One rung, one weight, ONE tracking, everywhere: → Ui.Caption at 600
    /// (12 / 16 / 600) + <see cref="EyebrowTracking"/>.
    /// <para>CASE IS NOT PART OF THE VOICE. The role used to be ALL-CAPS + heavy letterspacing, which is neither Fluent
    /// (sentence case everywhere) nor the editorial/Zune register the rest of the app aims at — and a
    /// <c>.ToUpper()</c> on a LOCALIZED string is worse than a style mistake: it mangles Turkish dotted i, expands
    /// German ß, and shouts a user's own display name back at them. So the alias takes the string's OWN casing and no
    /// call site may caps-transform it.</para>
    /// <para>COLOUR still belongs to the call site — an accent reason, a tertiary kind tag and an on-accent badge are
    /// the same type doing three different jobs, and the accent arm is deliberate identity (see the accent-roles
    /// section in <c>WaveeTokens</c>). Metrics and tracking are the alias's; colour is not.</para>
    /// <para>Text that merely wants this RUNG without being an eyebrow — a rank numeral, a podium tile's artist name, a
    /// day heading — reads <c>Ui.Caption(x) with { Weight = 600 }</c> straight off the factory (same metrics, same line
    /// height, no tracking) rather than claiming to be a label it is not. One alias per ROLE, not one per rung.</para></summary>
    public static TextEl Eyebrow(string s) => Ui.Caption(s) with { Weight = 600, CharSpacing = EyebrowTracking };

    /// <summary>"Because you played…" section / rail headers. → Ui.Subtitle (20 / 28 / 600).</summary>
    public static TextEl RailHeader(string s) => Ui.Subtitle(s);

    /// <summary>A Home MODULE header — the same 20/28 Semibold metrics as <see cref="RailHeader"/> but set in the
    /// DISPLAY face with a hair of negative tracking. That is the whole difference between a shelf label and a module
    /// title: at 20px the display face's tighter fit and optical sizing make a stack of thirteen headings read as
    /// typography rather than as thirteen repeated UI labels.</summary>
    public static TextEl ModuleHeader(string s) => Ui.Subtitle(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        CharSpacing = -6f,
    };

    /// <summary>A module header plus its subdued fact ("Radio · 20 stations"), shaped as ONE paragraph so the 12px run
    /// sits on the 20px run's real baseline. Two separate text nodes cannot do this — the engine's FlexAlign has no
    /// Baseline member, so side-by-side nodes can only be bottom-aligned, which puts the small run a couple of pixels
    /// low and reads as a mistake at this size. Same construction as <see cref="RailHeader(string,string)"/>, in the
    /// display face.</summary>
    public static SpanTextEl ModuleHeader(string title, string meta)
    {
        var heading = Ui.Subtitle("");
        var caption = Ui.Caption("");
        return new SpanTextEl(
        [
            new TextSpan(title),
            // Two spaces, not a separator glyph: the prototype's 12px gap between the title and its subtitle. A run
            // break cannot carry margin, so the space IS the gap.
            new TextSpan("  " + meta, Weight: caption.ResolvedWeight, Color: Tok.TextTertiary, Size: caption.Size),
        ])
        {
            FontFamily = "Segoe UI Variable Display",
            CharSpacing = -6f,
            Size = heading.Size,
            Weight = heading.ResolvedWeight,
            LineHeight = heading.LineHeight,
            LineStacking = heading.LineStacking,
            LineBounds = heading.LineBounds,
            Wrap = TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis,
            MaxLines = 1,
            MinWidth = 0f,
            Shrink = 1f,
        };
    }

    /// <summary>A rail heading plus baseline-aligned compact metadata. Both runs shape as one paragraph, so the smaller
    /// suffix shares the heading's baseline instead of bottom-aligning two unrelated line boxes.</summary>
    public static SpanTextEl RailHeader(string title, string meta)
    {
        var heading = Ui.Subtitle("");
        var caption = Ui.Caption("");
        return new SpanTextEl(
        [
            new TextSpan(title),
            new TextSpan("  " + meta, Weight: caption.ResolvedWeight,
                Color: Tok.TextTertiary, Size: caption.Size),
        ])
        {
            Size = heading.Size,
            Weight = heading.ResolvedWeight,
            LineHeight = heading.LineHeight,
            LineStacking = heading.LineStacking,
            LineBounds = heading.LineBounds,
            Wrap = TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis,
            MaxLines = 1,
            MinWidth = 0f,
            Shrink = 1f,
        };
    }

    /// <summary>Page hero (playlist / album name). → Ui.Title (28 / 36 / 600).</summary>
    public static TextEl PageHero(string s) => Ui.Title(s);

    /// <summary>The two-column detail rail's identity title — and the vertical hero's. <see cref="PageHero"/>'s
    /// 28/36/600 engine-ramp metrics, in the DISPLAY face with −20/1000 em tracking (−0.8px at the shell's 40px
    /// TitleLarge override). One alias so playlist, liked, and album rails share a voice; the shell still swaps
    /// Size/LineHeight to 40/52 when the window is tall.</summary>
    public static TextEl DetailHero(string s) => Ui.Title(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        CharSpacing = -20f,
    };

    /// <summary>A LIBRARY SURFACE's masthead — the name of a place rather than of a record ("Recents"). One rung above
    /// <see cref="PageHero"/> on the SAME engine ramp (Ui.TitleLarge, 40 / 52), set in the display face at the LIGHT
    /// weight so the word reads as typography over a Mica wash instead of as one more bold UI label.
    /// <para>Deliberately NOT a fourth display-face divergence: it keeps the ramp's size/line-height pair and stays
    /// inside the 400/600 weight policy — the face and the hair of negative tracking are the same two liberties
    /// <see cref="ModuleHeader(string)"/> already takes, and nothing more.</para></summary>
    public static TextEl SurfaceDisplay(string s) => Ui.TitleLarge(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Weight = 400,
        CharSpacing = -12f,
    };

    /// <summary>Wide artist identity display: a larger, tightly tracked Display face for editorial heroes.
    /// 84 / 96 / <b>700</b> — one of the three sanctioned display-face divergences (see the class header).</summary>
    public static TextEl ArtistDisplay(string s) => Ui.Display(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Size = 84f,
        LineHeight = 96f,
        Weight = 700,
        CharSpacing = -28f,
        MinSize = 68f,
    };

    /// <summary>Medium artist identity title retaining the display face and weight under pressure.
    /// 48 / 60 / <b>700</b> — sanctioned display-face divergence.</summary>
    public static TextEl ArtistTitle(string s) => Ui.TitleLarge(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Size = 48f,
        LineHeight = 60f,
        Weight = 700,
        CharSpacing = -20f,
        MinSize = 40f,
    };

    /// <summary>Compact artist identity title with the same bold, tightly tracked voice.
    /// 32 / 40 / <b>700</b> — sanctioned display-face divergence.</summary>
    public static TextEl ArtistCompactTitle(string s) => Ui.Title(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Size = 32f,
        LineHeight = 40f,
        Weight = 700,
        CharSpacing = -12f,
        MinSize = 28f,
    };

    /// <summary>Now-playing track title. → Ui.Subtitle (20 / 28 / 600).</summary>
    public static TextEl NowPlayingTitle(string s) => Ui.Subtitle(s);

    /// <summary>The artist SPEAKING — the pick card's quote, set as editorial typography rather than a UI label.
    /// → Ui.Title's ramp pair (28 / 36) in the display face at REGULAR 400: stays on the engine ramp and inside the
    /// 400/600 weight policy, taking only the same two liberties <see cref="SurfaceDisplay"/> already takes (the
    /// display face + a hair of negative tracking). Regular at 28px is what makes a first-person sentence read as a
    /// voice instead of a heading; the string keeps the artist's OWN casing (see <see cref="Eyebrow"/> — no
    /// case-transform on authored text).</summary>
    public static TextEl PickQuote(string s) => Ui.Title(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Weight = 400,
        CharSpacing = -12f,
    };

    /// <summary>The Fold tile's Zune crop — the SAME cut as <see cref="PickQuote"/> (Ui.Title 28/36 in the display face,
    /// Weight 400, CharSpacing -12). Named for the surface it sits on rather than for the voice it carries: a Fold tile's
    /// title is a card headline, not an artist speaking, so it does not borrow <see cref="PickQuote"/>'s name even though
    /// it borrows every one of its numbers. The prototype's own cut for this role (<c>font-weight:300; font-size:32px</c>)
    /// is deliberately NOT copied — 300 is off the house's 400/600 weight policy (see the class header) and 32px has no
    /// rung on the engine's type ramp; PickQuote's already-sanctioned 28/36/400 divergence covers the same "regular,
    /// display-face, editorial" register without adding a second weight exception.</summary>
    public static TextEl FoldTitle(string s) => PickQuote(s);

    /// <summary>A ZUNE-style pivot tab header (All / Music / Podcasts / Artists) — the Display face at SemiLight, one
    /// rung under <see cref="RailHeader(string)"/>. 19 / 25 / <b>350</b> — the second sanctioned weight divergence (see
    /// the class header): the same SemiLight the engine's own <c>Pivot</c> control uses for its header row, carried
    /// into the app's display face so the tab strip reads as editorial typography rather than as a WinUI chrome
    /// control.</summary>
    public static TextEl PivotLabel(string s) => Ui.BodyLarge(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Size = 19f,
        LineHeight = 25f,
        Weight = 350,
        CharSpacing = -6f,
    };

    /// <summary>NPV lyrics-peek reel — Subtitle's 20/28 pair in the display face at SemiLight 350 (same sanctioned
    /// cut as <see cref="PivotLabel"/>). The thin Zune voice of the daylist digits, at a line-readable size.</summary>
    public static TextEl NpvLyric(string s) => Ui.Subtitle(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Weight = 350,
        CharSpacing = -6f,
    };

    /// <summary>A GLANCEABLE STAT — one number the reader is meant to take in without reading: a play count, a tempo,
    /// a key, a duration. → Ui.Title's ramp pair (28 / 36) in the display face at SemiLight <b>350</b>, the THIRD and
    /// last use of the sanctioned 350 cut (see the class header; <see cref="PivotLabel"/> and <see cref="NpvLyric"/>
    /// are the other two). A stat is the same register those already occupy — Zune numerals, not a UI label — so it
    /// borrows their weight rather than opening a fourth divergence.
    ///
    /// <para>WHY THE TITLE RUNG AND NOT SUBTITLE. The stat has no heading above it: it IS the heading, sitting over
    /// its own sentence-case caption, and the whole point of dropping the tiles that used to box these values is that
    /// the numerals must now carry the section by themselves. At Subtitle's 20/28 a play count and the 13.5px version
    /// rows below it are close enough to read as one undifferentiated column of text — which is the wall the tiles
    /// were there to break up. 28/36 is the smallest rung that still reads as a display figure at this density, and it
    /// is a rung, so the line height comes with it (the three-part contract above).</para>
    ///
    /// <para><paramref name="unit"/> is the small run that shares the numeral's BASELINE — "2B" big, "F♯ major" small —
    /// built as ONE paragraph for the same reason <see cref="ModuleHeader(string,string)"/> is: FlexAlign has no
    /// Baseline member, so two side-by-side nodes can only bottom-align, which drops the small run a few pixels and
    /// reads as a mistake at a 16px size difference. Null (the common case) yields the numeral alone.</para></summary>
    public static SpanTextEl StatHero(string value, string? unit)
    {
        var heading = Ui.Title("");
        var caption = Ui.Caption("");
        TextSpan[] spans = unit is { Length: > 0 }
            ? new TextSpan[]
              {
                  new(value),
                  // Two spaces, not a separator glyph — the same gap-as-a-run trick ModuleHeader uses, because a run
                  // break cannot carry margin.
                  new("  " + unit, Weight: caption.ResolvedWeight, Color: Tok.TextSecondary, Size: caption.Size),
              }
            : new TextSpan[] { new(value) };
        return new SpanTextEl(spans)
        {
            FontFamily = "Segoe UI Variable Display",
            CharSpacing = -6f,
            Size = heading.Size,
            Weight = 350,
            LineHeight = heading.LineHeight,
            LineStacking = heading.LineStacking,
            LineBounds = heading.LineBounds,
            Wrap = TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis,
            MaxLines = 1,
            MinWidth = 0f,
            Shrink = 1f,
        };
    }
}
