// ── Shell/Rail.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// what the right rail still builds in the core: the lyrics header (until T7b), the "Next up" rows the now-playing body and the video
// body share, and the shared reads (the playing track, its cover, the hero wash)
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 1400 lines
// Spec: ch 21 §9.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// T6b MOVED THE PICTURE OF THE PANEL TO THE STRUCTURE. The rail's frame (docked + floating), its header, the docked video cap's
// body, the NowPlaying arm and the other arms are `Shell.Panel` (`Themes\Classic\Structure\Views\Panel\ClassicPanel.cs`); the hero
// tile, its header row, the art menu and the now-playing body are `Player.NowPlayingHero` (`ClassicNowPlayingHero.cs`); the friends
// body is `Panel.Friends` (`ClassicFriends.cs`); the style flyout, the twelve mini-arts and the swatches are `Player.DeckStyleFlyout`
// (`ClassicDeckFlyout.cs`, `Rules\DeckThumbnails.cs`). The core half (what each view reads, what each click asks for) is
// `RailWiring` (`Shell/Rail.Wiring.cs`); the state machine stays `Shell.PanelHost` and the decisions stay `Rail.VideoCoupling`.
//
// T7c MOVED THE VIDEO BODY (the track meta, the "Up next" eyebrow, the "no video for this song" placeholder) to the structure
// (`ClassicVideoPanelBody`, `Themes\Classic\Structure\Views\Video`); the docked card is the `Video.Docked` key.
//
// WHAT IS STILL HERE is what other lanes own and the panel receives as builders: `Rail.LyricsHeader()` (T7b: the lyrics lane owns
// its header, with the AI sparkle and the inspector button, both of which call `Rail.HeaderButton`) and the track-row "Next up" list
// (T8a), which Classic's now-playing and video bodies place. The queue body is the core host
// `Queue.RailPanelHost` (`Entities/Queue.UI.cs`), which installs itself through `Rail.QueueBody` and mounts the `Queue.Panel` view (T7d).
//
// Mounted from other stage-B files: `Deck.View()` (K2), `Lyrics.NpvPeek()` and `Lyrics.View()` (K3).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Rail
{
    /// <summary>The Queue body — the core host of the rail's queue panel (`Entities/Queue.UI.cs`, `Queue.RailPanelHost`) installs
    /// itself here at composition. Until then the rail shows the queue's empty grammar.</summary>
    public static Func<Element>? QueueBody { get; set; }

    /// <summary>The queue mode's body: the installed queue panel, or the queue's empty grammar.</summary>
    internal static Element QueueBodyOrEmpty() => QueueBody?.Invoke()
        ?? Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                            title: Loc.Get(Strings.Player.QueueEmpty), subtitle: "");

    /// <summary>Row 1 of the icon-button table pairs the 32-square with a 16-DIP glyph (never 12).</summary>
    internal const float HeaderGlyph = 16f;

    // ── the lyrics header (the lyrics lane's; the panel places it) ───────────────────────────────────────────────────

    /// <summary>The lyrics mode's header.</summary>
    internal static Element LyricsHeaderElement() => Embed.Comp(static () => new LyricsHeader());

    static BoxEl HeaderBox(Element[] kids) => new()
    {
        Direction = 0, Height = Design.Size.NavItemH, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
        Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f), Children = kids,
    };

    /// <summary>Title · (secondary-line toggle) · (AI sparkle) · expand · close. Its own component so the capability and the prefs epoch
    /// subscribe the HEADER, not the whole rail. The toggle is composed only when the document on screen carries a
    /// second layer; the expand button is returned in BOTH arms — a track with no lyrics still opens a full stage.</summary>
    sealed class LyricsHeader : Component
    {
        public override Element Render()
        {
            var begin = UseContext(SharedTransition.Begin);   // the stage's art flight is captured before the mount (null headless)
            int available = Prefs.Lyrics.Available.Value;
            int secondary = Prefs.Lyrics.SecondaryLine();
            var kids = new List<Element>(6) { TitleText() };
            if (available != 0)
                kids.Add(HeaderButton(Icons.Globe, Prefs.Lyrics.Tooltip(secondary),
                    LyricsVerbs.Global.ToggleSecondLine,
                    active: (available & Prefs.Lyrics.BitFor(secondary)) != 0));
            // The on-device AI sparkle: its own component (only it subscribes the AI status), hidden while the feature is off.
            kids.Add(AiLyrics.HeaderButton());
            kids.Add(HeaderButton(Icons.FullScreen, Loc.Get(Playback.CurrentId.Value.Kind == EntityKind.Episode ? Strings.Podcast.Reader.Transcript : Strings.Player.ExpandLyrics), () => LyricsVerbs.Global.ExpandToStage(begin, "rail-header")));
            kids.Add(Diagnostics.LyricsInspector.Button());
            kids.Add(CloseButton());
            return HeaderBox(kids.ToArray());
        }
    }

    static Element TitleText() => R.Type.RailHeader(Loc.Get(Playback.CurrentId.Value.Kind == EntityKind.Episode ? Strings.Podcast.Reader.Transcript : Strings.Player.Lyrics)) with
    {
        Grow = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>A glyph button in the header. <paramref name="active"/> is the stateful arm (the accent tint is the only
    /// affordance a 32-DIP glyph has to say "on").</summary>
    internal static Element HeaderButton(string glyph, string tip, Action onClick, bool active = false) => ToolTip.Wrap(GlyphBox(glyph, onClick, active), tip);

    static BoxEl GlyphBox(string glyph, Action onClick, bool active) => new BoxEl
    {
        Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = onClick,
        Children =
        [
            new TextEl(glyph)
            {
                Size = HeaderGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                Color = active ? Tok.AccentTextPrimary : Tok.TextSecondary,
                HoverColor = active ? Tok.AccentTextPrimary : Tok.TextPrimary,
            },
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>✕ closes the RAIL for every mode, video included — the coupling demotes a docked video. No tooltip: a close
    /// glyph is universal.</summary>
    static Element CloseButton() => GlyphBox(Icons.ChromeClose, static () => Shell.PanelHost.Shared.Close(), active: false);

    // ── shared reads ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The playing TRACK — subscribes the playable and the track table — or an invalid handle (an episode, a
    /// module playable or nothing).</summary>
    internal static Track NowTrack()
    {
        var r = Playback.Current.Value;
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        _ = Entities.Current.Tracks.Changed.Value;
        return r.Kind == EntityKind.Track ? new Track(r.Slot) : default;
    }

    /// <summary>The playing track's cover url, read LIVE (for a bound thunk that must follow a track change).</summary>
    internal static string? NowArtUrl()
    {
        var r = Playback.Current.Value;
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        _ = Entities.Current.Tracks.Changed.Value;
        return r.Kind == EntityKind.Track ? Controls.ArtUrl(new Track(r.Slot).ImageId) : null;
    }

    /// <summary>THE hero wash — one function, three surfaces (the cover placeholder, the deck faces' art and the flyout's
    /// "Album colour" swatch), so the three can never grade the artwork differently. The palette watch is read INSIDE, so
    /// a late grading repaints a bound placeholder without re-rendering anything.</summary>
    public static ColorF HeroWashColor(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        ColorF accent = Design.SchemeFor(url) is { } s ? ColorScience.Lift(ColorScience.Accent(s)) : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, R.Wash.CoverMix);
    }

    /// <summary>The track's FIRST credited artist — the one the rail's About card and the fullscreen stage's Artist pane
    /// both read. <c>default</c> when the track is not (yet) valid or carries no artist edge.</summary>
    internal static Artist NowPlayingArtist(Track track)
    {
        if (!track.IsValid) return default;
        var slots = track.ArtistSlots;
        return slots.Length > 0 && slots[0] > 0 ? new Artist(slots[0]) : default;
    }

    // ── "Next up" rows (the NPV section and the Video body share them) ───────────────────────────────────────────────

    /// <summary>A "Next up" row's floor: 52 — the <c>Controls.Surface</c> row shape's 8-DIP padding lifts the 40-art row to
    /// 56, and the hidden-artwork row (the 32 play glyph) stays on this floor.</summary>
    const float NextUpRowH = 52f;

    /// <summary>The "Next up" rows, as ONE component: it owns the subscriptions the rows read (the queue edge, the track
    /// table — a title or an artist line landing late — and the artwork preference), so a table edge re-renders these five
    /// rows and not the panel that holds them.</summary>
    internal static Element NextUpList() => Embed.Comp(static () => new NextUpHost());

    sealed class NextUpHost : Component
    {
        public override Element Render()
        {
            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table reads below
            _ = Entities.Current.Edges.Queue.Changed.Value;
            bool art = !Prefs.Appearance.TrackArtworkHidden();
            Span<int> window = stackalloc int[Shell.NextUpWindow.Max];
            int n = Shell.NextUpWindow.Fill(window);
            var rows = new Element[n];
            var items = Queue.Rows;
            for (int k = 0; k < n; k++)
            {
                int i = window[k];
                rows[k] = NextUpRow(new Track(Queue.RefAt(i).Slot), i, items[i].ItemId, art);
            }
            return new BoxEl { Direction = 1, Gap = 2f, Children = rows };
        }
    }

    /// <summary>One "Next up" row on the shared media surface (<c>Shape.Row</c> over <c>Track.RowData</c>): the row plate,
    /// the hand, the play FAB over the art, a track drag and the right-click menu. The row CLICK plays that queue item —
    /// the same <c>play</c> the FAB runs (<see cref="Wavee.Theming.Contracts.IQueueVerbs.Play"/>, the queue panel's own skip), run through
    /// <c>Track.Invoke</c> so the deck row toggles pause instead. The item is resolved inside the LIVE queue when clicked:
    /// the surface keeps its data across a queue shift (a data-equal re-push carries no new delegate into the render), so
    /// a captured index would go stale. The key carries
    /// the queue item and the artwork flag, so a settings flip REMOUNTS rather than re-binds. Like every queue row it has
    /// no "…" button (<see cref="QueueRowRules.ShowsMenuButton"/>): the menu is the right-click.</summary>
    static Element NextUpRow(Track t, int queueIndex, ulong itemId, bool art)
    {
        // The queue panel's own skip (IQueueVerbs.Play): resolves the item in the LIVE queue at click time, trims
        // the rows skipped over and plays — so a rail click and a queue-panel click are the same act.
        Action play = () => Shell.QueueVerbs.Default.Play(itemId, queueIndex, new EntityRef(EntityKind.Track, t.Slot));
        Action click = () => Track.Invoke(t, play);
        var data = Track.RowData(t, new Track.RowDataOptions(OnClick: click, OnPlay: play, ShowArtwork: art,
                                                             ShowExplicit: false, ShowVideo: true,
                                                             ShowMenu: QueueRowRules.ShowsMenuButton));
        var shape = Shape.Row(TrackRowRules.ArtEdge(Design.Size.ArtThumb, art)) with { MinHeight = NextUpRowH };
        return Controls.Surface(data, shape) with
        {
            Key = (itemId != 0 ? "vrp:i" + itemId : "vrp:e" + queueIndex) + (art ? ":art" : ":noart"),
        };
    }
}
