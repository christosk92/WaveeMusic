using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Backend;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Album header artist control: WaveeMusic's AvatarStack translated to FluentGPU and made interactive.
// Visible stack = album-billed artists only. The "+N" badge counts track-only contributors, and tapping the stack opens
// a flyout of every distinct artist on the album.
//
// Portraits do not ride getAlbum (that persisted query ships billed artists as uri+name only). ArtistV4 Identity
// carries PortraitGroup, so this control asks the hydrator for those billed uris and reads the store back. Re-pushed
// props keep the billed set live across a route-reused DetailPage; constructor fields would freeze the first album.
sealed class ArtistFacePile : Component
{
    internal sealed record Props(
        IReadOnlyList<ArtistRef> Artists,
        IReadOnlyList<Artist>? AlbumArtists,
        IReadOnlyList<Track> Tracks,
        float MaxWidth,
        DetailHandlers Handlers);

    const float Avatar = 28f, Ring = 2f, Outer = Avatar + Ring * 2f, Overlap = 12f;
    const int MaxVisible = 4;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var svc = UseContext(Services.Slot);
        var overlay = UseContext(Overlay.Service);
        string billedKey = UriKey(p.Artists);
        // Identity hydrate is the re-render trigger: seed Resolve may already have a resident portrait (NPV about-artist,
        // a prior artist-page visit). When any billed avatar is still missing, one ArtistV4 batch fills the rest.
        var portraits = UseResource(async ct =>
        {
            var seed = ResolveBilled(p.Artists, p.AlbumArtists, svc?.RealStore);
            if (svc is null || !NeedsPortraitFetch(seed)) return 0;
            var uris = UrisOf(p.Artists);
            if (uris.Count == 0) return 0;
            await svc.Hydrator.EnsureManyAsync(uris, HydrationLevel.Identity, HydrationOptions.Default, ct)
                .ConfigureAwait(false);
            return 1;
        }, 0, billedKey);
        _ = portraits.Loadable.Value.Value;

        var billed = ResolveBilled(p.Artists, p.AlbumArtists, svc?.RealStore);
        var all = AllDistinctArtists(billed, p.Tracks, p.AlbumArtists, svc?.RealStore);
        var visible = billed.Count > 0 ? billed : all;
        int overflow = billed.Count > 0 ? Math.Max(0, all.Count - billed.Count) : Math.Max(0, all.Count - MaxVisible);

        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        if (all.Count == 0) return new BoxEl();

        void Toggle()
        {
            if (overlay is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => Flyout(all, p.Handlers, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        void Key(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Down or Keys.F4)
            {
                Toggle();
                e.Handled = true;
            }
        }

        var button = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f, Shrink = 0f,
            Padding = new Edges4(6f, 4f, 6f, 4f),
            Corners = CornerRadius4.All(8f), Fill = ColorF.Transparent,
            HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = Toggle, OnKeyDown = Key, OnRealized = h => anchor.Value = h,
            Children =
            [
                FaceStack(visible, overflow),
                Icon(Icons.ChevronDownSmall, 8f, Tok.TextTertiary),
            ],
        };

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MaxWidth = p.MaxWidth,
            Children =
            [
                ToolTip.Wrap(button, "View all artists"),
                ArtistLinks(visible, p.Handlers),
            ],
        };
    }

    Element FaceStack(IReadOnlyList<Artist> artists, int overflow)
    {
        int visible = Math.Min(MaxVisible, artists.Count);
        var kids = new List<Element>(visible + (overflow > 0 ? 1 : 0));
        for (int i = 0; i < visible; i++) kids.Add(AvatarFrame(artists[i], i == 0));
        if (overflow > 0) kids.Add(OverflowFrame(overflow, visible == 0));
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
    }

    static Element AvatarFrame(Artist a, bool first) => new BoxEl
    {
        Width = Outer, Height = Outer, Shrink = 0f, Corners = CornerRadius4.All(Outer / 2f),
        Fill = Tok.FillSolidBase, Padding = Edges4.All(Ring),
        Margin = new Edges4(first ? 0f : -Overlap, 0f, 0f, 0f),
        Children = [PersonPicture.Create("", Avatar, displayName: a.Name, imageSourcePath: a.Image?.Url)],
    };

    static Element OverflowFrame(int n, bool first) => new BoxEl
    {
        Width = Outer, Height = Outer, Shrink = 0f, Corners = CornerRadius4.All(Outer / 2f),
        Fill = Tok.FillSolidBase, Padding = Edges4.All(Ring),
        Margin = new Edges4(first ? 0f : -Overlap, 0f, 0f, 0f),
        Children =
        [
            new BoxEl
            {
                Width = Avatar, Height = Avatar, Corners = CornerRadius4.All(Avatar / 2f),
                Fill = Tok.FillCardDefault, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl("+" + n) { Size = 10f, Weight = 700, Color = Tok.TextSecondary }],
            },
        ],
    };

    static Element ArtistLinks(IReadOnlyList<Artist> billed, DetailHandlers h)
    {
        // ONE clickable, ellipsized run (to the lead artist). A long multi-artist string must truncate CLEANLY, never clip
        // under the scrollbar — the chevron's "view all artists" flyout is the per-artist escape hatch. Grow+Basis 0 so the
        // run fills the width left of the avatar pile; MaxLines 1 + ellipsis keeps it to one tidy line within the rail.
        if (billed.Count == 0) return new BoxEl();
        var lead = billed[0];
        bool enabled = lead.Uri.Length > 0;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < billed.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(billed[i].Name); }
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Shrink = 1f,
            OnClick = enabled ? () => h.Go("artist:" + lead.Uri, lead.Name) : null,
            Cursor = enabled ? CursorId.Hand : (CursorId?)null, Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text,
            Children = [new TextEl(sb.ToString()) { Size = 14f, Weight = 700, Color = Tok.AccentTextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
        };
    }

    static Element Flyout(IReadOnlyList<Artist> artists, DetailHandlers h, Action close)
    {
        var rows = new Element[artists.Count];
        for (int i = 0; i < artists.Count; i++)
        {
            var a = artists[i];
            rows[i] = new BoxEl
            {
                Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                Corners = CornerRadius4.All(6f),
                Role = AutomationRole.MenuItem, Focusable = true, Cursor = a.Uri.Length > 0 ? CursorId.Hand : (CursorId?)null,
                OnClick = a.Uri.Length > 0 ? () => { h.Go("artist:" + a.Uri, a.Name); close(); } : null,
                Children =
                [
                    PersonPicture.Create("", 32f, displayName: a.Name, imageSourcePath: a.Image?.Url),
                    new TextEl(a.Name) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            }.Interactive(Interaction.Subtle);
        }

        var list = new BoxEl { Direction = 1, Gap = 2f, Width = 264f, Children = rows };
        return new BoxEl
        {
            Direction = 1, Width = 280f, MaxHeight = 360f,
            Padding = new Edges4(8f, 8f, 8f, 8f),
            Children = [ScrollView(list) with { Width = 264f, MaxHeight = 344f, ContentSized = true, AutoEdgeFade = true, Grow = 0f }],
        };
    }

    static IReadOnlyList<Artist> ResolveBilled(IReadOnlyList<ArtistRef> billedRefs, IReadOnlyList<Artist>? billedDetailed, IStore? store)
    {
        var result = new List<Artist>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var detailed = IndexByUri(billedDetailed);
        foreach (var ar in billedRefs)
        {
            if (ar.Uri.Length == 0 || !seen.Add(ar.Uri)) continue;
            result.Add(ResolveOne(ar, detailed, store));
        }
        return result;
    }

    static IReadOnlyList<Artist> AllDistinctArtists(IReadOnlyList<Artist> billed, IReadOnlyList<Track> tracks,
        IReadOnlyList<Artist>? billedDetailed, IStore? store)
    {
        var result = new List<Artist>(billed);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < billed.Count; i++)
            if (billed[i].Uri.Length > 0) seen.Add(billed[i].Uri);
        var detailed = IndexByUri(billedDetailed);

        foreach (var t in tracks)
            foreach (var ar in t.Artists)
            {
                if (ar.Uri.Length == 0 || !seen.Add(ar.Uri)) continue;
                result.Add(ResolveOne(ar, detailed, store));
            }
        return result;
    }

    static Dictionary<string, Artist> IndexByUri(IReadOnlyList<Artist>? artists)
    {
        var detailed = new Dictionary<string, Artist>(StringComparer.Ordinal);
        if (artists is { Count: > 0 })
            for (int i = 0; i < artists.Count; i++)
                if (artists[i].Uri.Length > 0) detailed[artists[i].Uri] = artists[i];
        return detailed;
    }

    static Artist ResolveOne(ArtistRef ar, Dictionary<string, Artist> detailed, IStore? store)
    {
        detailed.TryGetValue(ar.Uri, out var fromAlbum);
        var fromStore = store?.GetArtist(ar.Uri);
        var image = fromAlbum?.Image ?? fromStore?.Image;
        string name = fromAlbum is { Name.Length: > 0 } ? fromAlbum.Name
            : ar.Name.Length > 0 ? ar.Name
            : fromStore?.Name ?? "";
        if (fromAlbum is not null) return fromAlbum with { Name = name, Image = image };
        if (fromStore is not null) return fromStore with { Name = name.Length > 0 ? name : fromStore.Name, Image = image ?? fromStore.Image };
        return new Artist(ar.Id, ar.Uri, name, image);
    }

    static bool NeedsPortraitFetch(IReadOnlyList<Artist> billed)
    {
        for (int i = 0; i < billed.Count; i++)
            if (billed[i].Image is null && billed[i].Uri.Length > 0) return true;
        return false;
    }

    static List<string> UrisOf(IReadOnlyList<ArtistRef> billed)
    {
        var uris = new List<string>(billed.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ar in billed)
            if (ar.Uri.Length > 0 && seen.Add(ar.Uri)) uris.Add(ar.Uri);
        return uris;
    }

    static string UriKey(IReadOnlyList<ArtistRef> billed)
    {
        if (billed.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < billed.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(billed[i].Uri);
        }
        return sb.ToString();
    }
}
