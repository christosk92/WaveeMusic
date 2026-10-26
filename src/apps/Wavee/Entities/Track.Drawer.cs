// ── Entities/Track.Drawer.cs ───────────────────────────────────────────────────────────────────────────────────────
// the expanded-row drawer BODY: the facts strip, "Versions and formats" (the connector rail, the self row, the reserved
// music-video row, alternate audio), the waveform, the format split button + its radio ladder, and the View-credits sheet's
// host (its body is `Dialog.Sheet`, T11a)
//
// Role: UI (+ the pure `DrawerRules` section, pinned by Wavee.Tests/TrackDrawerRulesTests.cs)
// Owner: M
// Wave: 5
// Budget: 700 lines
// Spec: ch 01 W14, W15, W26, W27, §3 drawer rows, §7 DATA GAPS 1/13/14, §9 trap 8 · ch 04 §5 drawer motion, items 49-52,
//       68 · ch 05 W14 · 0.2.9 `Components/{TrackVersionsPanel, TrackFactsStrip, FormatSplitButton}.cs`,
//       `Actions/TrackCreditsDialog.cs`
//
// WHO MOUNTS WHAT. The TABLE owns the drawer's mount, keying ("drawer-body:" + rowKey), clip, zebra parity, reveal and
// indent (`Track.Table.cs` DrawerBox). This file owns only the BODY a page hands the table through `TableProfile.Drawer`
// (`DrawerSeam` for the album page): ONE component re-pushed a data-only props record, subscribed to exactly the tables it
// paints (the track, its album, the adder, the tags/versions/waveform relations, the deck's current item).
//
// THE DEMAND (on mount, per track, per scope): TrackFields.Audio | Tags | Isrc | Files | Video, the TrackVersions /
// TrackWaveform / TrackCredits edges, and — as versions land — Identity | Audio | Files over the version targets.
//
// THE RESERVED VIDEO ROW (ch 01 §9 trap 8): a track the catalogue says HAS a video reserves the 76×43 row under the key
// "v:video" BEFORE the versions relation answers, so the reveal targets its final height on the seed frame and the real
// row PATCHES into the reserved node (`DrawerRules.VideoRowFor`; a failed relation reserves nothing, W26).
//
// THE FORMAT OVERRIDE (ch 01 DATA GAP 14): the radio ladder reads/writes `Playback.FormatOverrideFor` /
// `Playback.SetFormatOverride` — a persisted per-uri map the PLAYBACK host owns (shared-file patch). Its wire codec and the
// format → rung map are `DrawerRules` here, so they are pure and tested.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using ViewSlots = FluentGpu.Theming.Slots;

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

    /// <summary>The overlay host the format ladder and the credits sheet open on; null ⇒ both are inert, never a crash.
    /// ROOT-LIFETIME state (<see cref="RootScope"/>): the host belongs to the mounted root, so a remounted root starts with none
    /// until its first page publishes its own.</summary>
    public static IOverlayService? DrawerOverlay { get => s_drawerOverlay.Value; set => s_drawerOverlay.Value = value; }
    static readonly RootState<IOverlayService?> s_drawerOverlay = new("Track.DrawerOverlay");

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

    sealed class DrawerHost : Component
    {
        DrawerProps? _latest;
        float[]? _peaks;
        int _peaksParent = -1;
        uint _peaksVersion, _peaksEpoch;
        readonly Action _demand, _demandVersions;
        // False until the first body is built: what the drawer opens WITH rides its clip reveal as part of the drawer;
        // only a fact that lands while it is open fades up (a second motion on top of the reveal read as a fade-pop).
        bool _opened;
        // The structure's drawer templates (T8c), resolved once per host from the ambient registry.
        TrackDrawerStyle? _style;
        // The core's format split button for one version (a component with the radio ladder and its overlay), handed to the body template.
        readonly Func<Track, Track, byte, Element> _formatButton;

        public DrawerHost()
        {
            _demand = Demand;
            _demandVersions = DemandVersions;
            _formatButton = (version, parent, kind) =>
                Embed.Comp(new FormatProps(_style!, version, parent, kind), static () => new FormatSplitHost()) with { Key = "fmt:" + version.Uri.Text };
        }

        public override Element Render()
        {
            var p = UseProps<DrawerProps>();
            _latest = p;
            var registry = UseContext(global::FluentGpu.Theming.ViewRegistry.Context);
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
            var style = _style ??= ListStyleLookup.Of(registry, StyleKeys.TrackDrawer);
            if (style is null) return new BoxEl();

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

            var facts = FactsFor(t, p.Options);
            bool arriving = _opened;
            _opened = true;
            float[]? peaks = PeaksFor(edges, t.Slot, epoch);
            var drawn = new TrackDrawerEntry[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var (kind, version) = entries[i];
                bool reserved = kind == DrawerRules.VideoKind && !version.IsValid;
                string rowKey = kind == DrawerRules.VideoKind ? DrawerRules.VideoRowKey : version.Uri.Text;
                bool isNow = !reserved && !playing.IsEmpty && playing == version.Id;
                drawn[i] = new TrackDrawerEntry(kind, version, rowKey, isNow, reserved, i == 0 ? peaks : null);
            }
            return style.Body(new TrackDrawerBodyArgs
            {
                Track = t, Facts = facts, Arriving = arriving, Entries = drawn,
                FormatButton = _formatButton, PlayVersion = PlayVersion,
            });
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

        // The waveform's 0..1 peaks, re-derived only when the relation's version moves (never per render): the kept
        // WaveSample triples reduced to the drawer's 220 columns at READ time (WaveformBands.ToColumns).
        float[]? PeaksFor(Edges edges, int parent, uint epoch)
        {
            if (edges.TrackWaveform.State(parent) == EdgeState.Unknown) return null;
            uint version = edges.TrackWaveform.Version(parent);
            if (_peaks is null || _peaksParent != parent || _peaksVersion != version || _peaksEpoch != epoch)
            {
                var columns = new float[WaveformBands.Columns];
                _peaks = WaveformBands.ToColumns(edges.TrackWaveform.Payload(parent), columns) ? columns : [];
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
        // No uri-fragment stand-in: an unresolved adder states nothing rather than a guess that reads as data.
        return Facts.For(in input, new FactsOptions(
            TempoPending: !t.Knows(TrackFields.Audio), PlaysPending: !t.Knows(TrackFields.PlayCount), HasVideo: t.HasVideo,
            AddedByName: byName, AddedByProfile: byName is null ? default : by,
            Culture: CultureInfo.CurrentCulture, Zone: TimeZoneInfo.Local,
            MajorWord: Loc.Get(Strings.Detail.TrackFacts.Major), MinorWord: Loc.Get(Strings.Detail.TrackFacts.Minor)));
    }

    // ══ 4. VERSIONS AND FORMATS (ch 01 W14/W15, ch 05 W14, ch 04 item 68) ════════════════════════════════════════════

    /// <summary>Open an entity's page by uri: the quiet album link of the facts strip (a template never names <c>Shell</c>).</summary>
    internal static void OpenEntity(EntityUri uri, string name) => Shell.GoTo(Shell.For(uri, name));

    // Play the FORM the user clicked: a music video plays through the PARENT song (kind 99 keys the video on the song —
    // DetailTracks.cs:3329-3342); an alternate recording plays itself. The deck's own item toggles.
    static void PlayVersion(Track version, Track parent, byte kind)
    {
        var target = kind == DrawerRules.VideoKind ? parent : version;
        if (target.IsValid) Invoke(target, () => Playback.PlayContext(target.Id));
    }

    // ── the format split button: [30 × 28 play | 20 × 28 caret] keyed "fmt:" + uri ──

    sealed record FormatProps(TrackDrawerStyle Style, Track Version, Track Parent, byte Kind);

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
            return _latest.Style.FormatSplit(new TrackDrawerFormatArgs { Play = _play, Open = _open, OnRealized = _capture });
        }

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
            var handle = overlay.Open(_anchorOf, () => MenuFlyout.Create(items, _close, size: OverlayFit.MenuSize), FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.ClosedAction = () => { if (ReferenceEquals(cell.Value, handle)) cell.Value = null; };
            cell.Value = handle;
        }
    }

    // ══ 5. THE VIEW-CREDITS SHEET (ch 01 W27, item 74) ═══════════════════════════════════════════════════════════════

    sealed record CreditsProps(Track Track);

    [CoreHost]
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
            // Bars while unanswered — never an empty sheet that fills in (W27): the view (`Dialog.Sheet`) draws the three states.
            var state = DrawerRules.CreditsViewFor(credits.Readiness(parent), credits.Count(parent));
            return ViewSlots.View(ViewKeys.DialogSheet, new CreditsSheetProps
            {
                State = state,
                Entries = state == DrawerRules.CreditsView.List ? CreditEntries(scope.Edges, parent) : [],
                OpenArtist = static slot => GoToArtist(new Artist(slot)),
            });
        }
    }

    // The server's group headings in its own casing (never re-cased), one entry per contributor, in the order and the groups the rules decide.
    static CreditsEntry[] CreditEntries(Edges edges, int parent)
    {
        var targets = edges.TrackCredits.Targets(parent);
        var rows = edges.TrackCredits.Payload(parent);
        var keys = new string[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            keys[i] = DrawerRules.CreditGroupKey(Entities.Strings.Resolve(rows[i].Group), Entities.Strings.Resolve(rows[i].Role));
        var order = new int[rows.Length];
        int n = DrawerRules.CreditOrder(keys, order);
        var entries = new CreditsEntry[n];
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            string? heading = keys[i].Length > 0 && DrawerRules.StartsGroup(keys, order.AsSpan(0, n), k) ? keys[i] : null;
            entries[k] = new CreditsEntry(heading, Entities.Strings.Resolve(rows[i].Name), Entities.Strings.Resolve(rows[i].Role), targets[i]);
        }
        return entries;
    }

    // ══ 6. THE PURE SECTION — DrawerRules (Wavee.Tests/TrackDrawerRulesTests.cs) ═════════════════════════════════════

    /// <summary>Every drawer decision that is not layout: version order, the reserved video row, format labels / ladder
    /// order / decodability / rung, the persisted override codec, credit grouping and the sheet's state. No engine type.</summary>
    public static class DrawerRules
    {
        public const float VideoThumbW = 76f, VideoThumbH = 43f, AudioThumb = 43f;
        /// <summary>The connector gutter, the rail's x inside it (= the indent the table subtracts) and the stub.</summary>
        public const float GutterW = 20f, RailX = RowMetrics.RailOffset, StubW = 9f;
        /// <summary>A version row: the 43 thumb + 4 above + 4 below (ch 04 item 68).</summary>
        public static float RowH => AudioThumb + 2f * Spacing.XS;
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
