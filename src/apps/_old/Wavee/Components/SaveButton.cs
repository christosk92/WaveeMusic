using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The Mutations-facet affordances (docs/plans/wavee/architecture.md §8 item 3, "UI affordances are capability-driven").
// Both read the live
// saved-set off LibraryBridge so they re-skin the instant the state flips (optimistic) and survive a restart (persisted).
// They render NOTHING when no Mutations source is connected — the affordance is GATED on the declared capability, not
// hardcoded. Used by the detail rail, the track rows' player bar, the artist page and the about-artist card.

/// <summary>A like / save heart — filled (accent) when the uri is saved, outline otherwise. For tracks (like) + albums (save).</summary>
sealed class SaveButton : Component
{
    readonly string _uri;
    readonly float _glyph;
    readonly float _box;
    readonly string? _name;   // display-only: names the item in the notification-center activity entry
    public SaveButton(string uri, float glyph = 16f, float box = 40f, string? name = null) { _uri = uri; _glyph = glyph; _box = box; _name = name; }
    /// <summary>Filled-heart ink. A thunk because the detail hero derives its accent from art / a Home payload that
    /// can land after mount (PreSaveButton's contract). Null → the page's <see cref="WaveeAccentCtx"/>, else the
    /// semantic accent token.</summary>
    public Func<ColorF>? Accent { get; init; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        var ctx = UseContext(WaveeAccentCtx.Slot);
        if (lib is null) return new BoxEl();                 // no Mutations source → no affordance (capability gate)
        bool saved = lib.IsSaved(_uri);                      // subscribe → re-skin on any saved-set change
        ColorF ink = Accent?.Invoke() ?? (ctx is { } a ? a.Value.Ink : Tok.AccentTextPrimary);
        return new BoxEl
        {
            Width = _box, Height = _box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(_box / 2f),
            HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
            Role = AutomationRole.Button,
            OnClick = () => lib.ToggleSaved(_uri, _name),
            Children = [Icon(saved ? Icons.HeartFill : Icons.Heart, _glyph, saved ? ink : Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
    }
}

/// <summary>Pre-save / Pre-saved — the heart for something that is not out yet. Takes EITHER uri kind and resolves the
/// <c>spotify:prerelease:</c> entity itself (extended-metadata kind 138), because that is the only entity the collection
/// write accepts and the artist page usually only knows the album. Renders nothing until it resolves, nothing when no
/// mutation source is connected (the same capability gate as <see cref="SaveButton"/>), and nothing when the release has
/// already dropped.
///
/// <para>PROPS FREEZE AT MOUNT (docs/design/subsystems/component-props-contract.md): a parent re-render does NOT re-run
/// the factory, so a caller whose uri can change must key the embed on it —
/// <c>Embed.Comp(() =&gt; new PreSaveButton { Uri = uri }) with { Key = "presave:" + uri }</c>.</para></summary>
sealed class PreSaveButton : Component
{
    /// <summary>Either scheme: <c>spotify:album:</c> (what the artist page holds) or <c>spotify:prerelease:</c> (what the
    /// write needs). The ids differ — see <see cref="PreReleaseUris"/> — so an album uri costs one kind-138 resolve.</summary>
    public required string Uri { get; init; }
    /// <summary>Display-only: names the item in the notification-center activity entry.</summary>
    public string? Name { get; init; }
    /// <summary>Accent for the call-to-action fill, so the pill belongs to the page it sits on. A thunk, not a value:
    /// the artist page derives its accent from art that lands AFTER the page mounts, and reading it inside Render
    /// subscribes — so the pill re-tints when the palette arrives instead of staying frozen at the mount-time default.</summary>
    public Func<ColorF>? Accent { get; init; }
    /// <summary>Label size; the glyph tracks it. Defaults match the release-masthead action row (ArtistPage.TopTracks).</summary>
    public float TextSize { get; init; } = 12f;

    public override Element Render()
    {
        // Hooks first and UNCONDITIONALLY — every early return below is after the last hook call.
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);

        // Already the write-addressable entity → no request at all. The loader still runs (hook discipline) but answers
        // null immediately, and a resolve failure/absence also lands as null, which renders nothing.
        bool direct = PreReleaseUris.IsPreRelease(Uri);
        var link = UseResource(ct => Resolve(svc, Uri, direct, ct), (PreReleaseLink?)null, Uri).Loadable.Value.Value;

        // The fast path has no date to check: a prerelease uri IS the announcement. The resolved path gates on the
        // wall-clock IsUpcoming, never on the link merely existing — a cached link outlives its own release.
        string? presaveUri = direct ? Uri : link is { IsUpcoming: true } ? link.PreReleaseUri : null;
        if (lib is null) return new BoxEl();                                // no Mutations source → no affordance (capability gate)
        if (presaveUri is not { Length: > 0 } target) return new BoxEl();   // still resolving, unresolvable, or already out

        bool saved = lib.IsSaved(target);        // subscribe → re-skin on any saved-set change (incl. the optimistic flip)
        ColorF fill = Accent?.Invoke() ?? Tok.AccentDefault;   // read inside Render → a late palette re-tints the pill

        // Two states, the release-masthead action grammar verbatim: the call to action is the accent-FILLED pill (the
        // Play slot), the engaged state is the bordered pill (the View slot) wearing the accent as ink.
        return new BoxEl
        {
            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(12f, 5f, 12f, 5f), Corners = CornerRadius4.All(4f),
            Fill = saved ? ColorF.Transparent : fill,
            BorderWidth = saved ? 1f : 0f, BorderColor = saved ? fill : ColorF.Transparent,
            // Engaged: an explicit hover fill, because auto-lighten has nothing to lighten over a transparent pill.
            // Call to action: left at the A==0 default so the recorder auto-lightens the accent (the Play pill's behaviour).
            HoverFill = saved ? Tok.FillSubtleSecondary : ColorF.Transparent,
            Cursor = CursorId.Hand, Role = AutomationRole.Button,
            OnClick = () => lib.ToggleSaved(target, Name),
            Children =
            [
                Icon(saved ? Icons.HeartFill : Icons.Heart, TextSize + 1f, saved ? fill : ColorContrast.PickContrast(fill)),
                new TextEl(Loc.Get(saved ? Strings.Detail.PreSaved : Strings.Detail.PreSave))
                {
                    Size = TextSize, Weight = 600, Color = saved ? fill : ColorContrast.PickContrast(fill), MaxLines = 1,
                },
            ],
        };
    }

    // The album→prerelease hop. Kind 138 answers to either key, so this is the ONE mapping between the two schemes;
    // nothing may synthesise one uri from the other. Offline the service is NullPreReleaseService → null → no pill.
    static Task<PreReleaseLink?> Resolve(Services? svc, string uri, bool direct, CancellationToken ct)
        => direct || svc is null ? Task.FromResult<PreReleaseLink?>(null) : svc.PreRelease.ResolveAsync(uri, ct);
}

/// <summary>A Follow / Following pill — for artists + playlists (the "save" verb for a profile). Accent border + text
/// when followed (AccentSelection: "you follow this" is a state, and the border carries it).
/// <para>THE ONLY follow control. SearchPage carried a second one — same words, same job, a different pill: 14/600 in a
/// Radii.Pill box with a 1px border, no glyph and no followed state, next to this one's 13/700 in an 18-radius box with
/// a 1.5px border. It is deleted; this is what a search hit's Follow renders now.</para>
/// <para>Geometry is WaveeCta's capsule (36 tall, Radii.Full, the Standard hover/press rung) because a Follow pill
/// stands beside a Play capsule on every artist hero — they have to be the same object at two jobs.</para></summary>
sealed class FollowButton : Component
{
    readonly string _uri;
    readonly string? _name;   // display-only: names the profile in the notification-center activity entry
    readonly ColorF? _foreground;
    public FollowButton(string uri, string? name = null, ColorF? foreground = null)
    { _uri = uri; _name = name; _foreground = foreground; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        if (lib is null) return new BoxEl();                 // capability gate
        bool following = lib.IsSaved(_uri);                  // subscribe
        ColorF idleInk = _foreground ?? Tok.TextPrimary;
        return new BoxEl
        {
            Direction = 0, Height = WaveeCta.PillHeight, Gap = Spacing.XS,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Corners = Radii.FullAll,
            BorderWidth = 1f, BorderColor = following ? Tok.AccentDefault
                : _foreground is { } fg ? fg with { A = 0.42f } : Tok.StrokeControlDefault,
            HoverFill = _foreground is { } hover ? hover with { A = 0.12f } : Tok.FillSubtleSecondary,
            PressedFill = _foreground is { } press ? press with { A = 0.18f } : Tok.FillSubtleTertiary,
            HoverScale = WaveeMotion.ScaleStandard.Hover, PressScale = WaveeMotion.ScaleStandard.Press,
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnClick = () => lib.ToggleSaved(_uri, _name),
            Children =
            [
                Icon(following ? Icons.HeartFill : Icons.Heart, 14f, following ? Tok.AccentTextPrimary : idleInk),
                Body(Loc.Get(following ? Strings.Artist.Following : Strings.Artist.Follow)) with
                    { Weight = 600, Color = following ? Tok.AccentTextPrimary : idleInk },
            ],
        };
    }

    // The skeleton shape the deriver walks (SkeletonProxy at the Embed.Comp site): the real pill so it shimmers as a
    // bordered pill, not a full-width default bar that would stretch across the actions row.
    public static Element SkeletonShape() => new BoxEl
    {
        Direction = 0, Height = WaveeCta.PillHeight, Gap = Spacing.XS,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Corners = Radii.FullAll,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children =
        [
            Icon(Icons.Heart, 14f, Tok.TextPrimary),
            Body(Loc.Get(Strings.Artist.Follow)) with { Weight = 600, Color = Tok.TextPrimary },
        ],
    };
}

/// <summary>The same follow toggle as a plateless TEXT ACTION, for the sticky text-chrome context band — which has no
/// plates in it at all, so the capsule above cannot go there (see <see cref="WaveeCta.TextAction"/> and its fence).
/// Same bridge, same handler, same words; ON is accent INK instead of an accent border.
/// <para>The uri freezes at mount like <see cref="FollowButton"/>'s, so every call site keys the embed on it.</para></summary>
sealed class FollowTextAction : Component
{
    readonly string _uri;
    readonly string? _name;   // display-only: names the profile in the notification-center activity entry
    public FollowTextAction(string uri, string? name = null) { _uri = uri; _name = name; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        if (lib is null) return new BoxEl();                 // capability gate
        bool following = lib.IsSaved(_uri);                  // subscribe
        return WaveeCta.TextAction(
            Loc.Get(following ? Strings.Artist.Following : Strings.Artist.Follow),
            () => lib.ToggleSaved(_uri, _name),
            toggledOn: following);
    }
}
