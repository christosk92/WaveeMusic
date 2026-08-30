using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// The <see cref="RailMode.Video"/> TAKEOVER body (docked-video design §5.3) — everything BELOW the shared pinned
/// card slot: the current track's own title/meta, a rule, and the next few queue entries. The card itself
/// (<see cref="Wavee.Features.Video.DockedVideoSurface"/>) is mounted by <c>RightRail</c> as a SIBLING above this
/// panel, not inside it — this panel starts exactly where the card ends.
///
/// <para><b>The B10 empty state.</b> When the current track has no video, <see cref="DockedVideoSurface"/>'s own
/// mount gate collapses it to nothing (freeing the decode surface — a track with no video has nothing to decode),
/// but <see cref="RailMode.Video"/> itself does NOT close: closing the rail on every non-video track in a mixed
/// playlist would be exactly the "per-track rail thrash" the design calls out. So this panel fills the card's vacated
/// slot with a static "no video for this song" placeholder — the same letterboxed-artwork composition
/// <see cref="DockedVideoSurface"/>'s own poster uses, MINUS the spinner (there is nothing to wait for; the answer is
/// already known). Track meta and Up next keep rendering underneath exactly as they would with video playing — the
/// user is still on this track, they only lose the frame.</para>
///
/// <para><b>Row reuse.</b> <c>QueuePanel</c>'s rows are NOT reused here: they are built around reorder/swipe/context-
/// menu machinery this read-only "next few" list has no use for, and QueuePanel is out of scope for this change
/// besides. <c>NowPlayingPanel</c>'s OWN "Next up" section already solves exactly this problem (a compact, read-only,
/// rail-width row) — but its builder is <c>private</c> to that file, which is also out of scope here. What IS shared
/// and genuinely reusable is the primitive both of them are built from: <see cref="TrackRow.ArtCard"/> with
/// <see cref="TrackRow.ArtCardKind.Rail"/>, the one row cell every surface in the app renders a track through. Reusing
/// the primitive (not a duplicate cell) keeps this panel's rows pixel-identical to the rail's other "next up" list
/// without touching either out-of-scope file.</para>
/// </summary>
sealed class VideoRailPanel : Component
{
    // Same shape as NowPlayingPanel's own NextCols (Features/Player/NowPlayingPanel.cs) — video glyph on, no
    // album/by/date/plays/heart/thumb columns: a rail-width row has room for art + title + artist only.
    static readonly ColumnSet UpNextCols = new(Album: false, By: false, Date: false, Video: true, Plays: false, Heart: false, Thumb: false);

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        bool showTrackArtwork = !AppearancePrefs.TrackArtworkHidden(svc?.Settings);
        if (b is null) return new BoxEl();

        var track = b.CurrentTrack.Value;
        bool hasVideo = b.CurrentTrackHasVideo.Value;   // subscribe → B10: react the instant the card upstream unmounts
        var upNext = b.Queue.Value
            .Where(e => e.Bucket is QueueBucket.UserQueue or QueueBucket.NextUp)
            .Take(5)
            .ToArray();

        var content = new List<Element>(8);

        if (!hasVideo)
            content.Add(NoVideoPlaceholder(track));

        if (track is not null)
        {
            content.Add(WaveeType.NowPlayingTitle(track.Title) with
            {
                MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
            });
            string meta = MetaLine(track);
            if (meta.Length > 0)
                content.Add(WaveeType.TrackMeta(meta) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        }

        content.Add(Rule());
        content.Add(WaveeType.Eyebrow(Loc.Get(Strings.Player.VideoUpNext)) with { Color = Tok.TextTertiary });

        if (upNext.Length == 0)
            content.Add(EmptyState.Compact(Loc.Get(Strings.Player.QueueEmpty)));
        else
        {
            var rows = new List<Element>(upNext.Length);
            foreach (var e in upNext)
                rows.Add(Row(b, lib, go, e, showTrackArtwork));
            content.Add(new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() });
        }

        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.S,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.M),
            Children = content.ToArray(),
        }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }

    // "Artist · Album" — deliberately NOT "Artist · Album · Year": Track/AlbumRef (Wavee.Core.Domain.Models) carry no
    // release-year field (AlbumRef is Id/Uri/Name only), and inventing one to match a mockup's illustrative text would
    // be exactly the kind of overclaim CLAUDE.md's honesty discipline rules out.
    static string MetaLine(Track t)
    {
        string artists = t.Artists.Count > 0 ? DetailFormat.ArtistNames(t.Artists) : "";
        string album = t.Album.Name;
        if (artists.Length == 0) return album;
        if (album.Length == 0) return artists;
        return artists + " · " + album;
    }

    static Element Rule() => new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeCardDefault };

    // The card's own poster composition (DockedVideoSurface.Poster), minus the spinner — there is nothing pending,
    // the answer ("this song has no video") is already known.
    static Element NoVideoPlaceholder(Track? track) => new BoxEl
    {
        Shrink = 0f, AspectRatio = 16f / 9f, ZStack = true, ClipToBounds = true,
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.MediaLetterbox,
        Children =
        [
            new BoxEl { Grow = 1f, Opacity = 0.4f, ClipToBounds = true, Children = [ Surfaces.ArtworkFill(track?.Image, 0f) ] },
            new BoxEl
            {
                Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = Edges4.All(Spacing.S),
                Children =
                [
                    new TextEl(Loc.Get(Strings.Player.NoVideoForThisSong))
                    {
                        Size = 12f, Weight = 600, Color = Tok.TextOnAccentPrimary,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            },
        ],
    };

    static Element Row(PlaybackBridge b, LibraryBridge? lib, Action<string, string?>? go, QueueEntry e, bool showArtwork)
    {
        var t = e.Track;
        var st = TrackRow.StateOf(b, lib, t);
        return new BoxEl
        {
            Key = "vrp:" + (e.ItemId.IsNone ? "e" + e.EntryId : "i" + e.ItemId.Value),
            Direction = 1, Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
            Children =
            [
                TrackRow.ArtCard(t, st, UpNextCols, go,
                    onPlay: () => TrackRow.Invoke(b, t, () => b.Player.PlayTrackAsync(t)),
                    art: WaveeSize.ArtThumb,
                    showArtists: true,
                    explicitBadge: false,
                    showDuration: false,
                    kind: TrackRow.ArtCardKind.Rail,
                    showArtwork: showArtwork),
            ],
        };
    }
}
