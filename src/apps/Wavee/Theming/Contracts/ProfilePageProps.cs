// ── Theming/Contracts/ProfilePageProps.cs — the props of the profile page view (themes plan 6.0, T10c2) ─────────────────────────
//
// The core host (`Profile.PageHost`) keeps the data and the decisions: the demands, the load face, the reveal latch, the section plan and each section's
// body verdict and cards, the latched hero metrics, the watched accent, the scroll spy, the shell-tint claim and every verb. A theme's view draws what this
// record carries. Every member is read-only (WVTH006); a writable cell travels as a `Func<Signal<T>>`, a live value as a read-only signal or a Func.
//
// The view may not name `Shell`, `Playback`, `Entities` or `Prefs` (WVTH002): what a card opens or plays, the hero's share and menu verbs and the account link
// are verbs here or the static `ProfileVerbs`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>What the hero states. <see cref="PlaceholderFor"/> is the shimmer's representative shape: its words are never shown (the deriver paints bars) and
/// its avatar is a plain bone circle.</summary>
public readonly record struct ProfileHeroText(string Name, string? AvatarUrl, uint PersonArgb, int Followers, int Following,
                                              int Playlists, bool Own, bool ShowFollows, bool Placeholder)
{
    /// <summary>The loaded person's facts.</summary>
    public static ProfileHeroText For(User u, bool own, string? avatarUrl)
        => new(u.Name, avatarUrl, ProfileTone.Argb(u.Color), u.Followers, u.Following, u.PublicPlaylists, own, u.ShowFollows, false);

    /// <summary>The shimmer's representative shape.</summary>
    public static ProfileHeroText PlaceholderFor(bool own)
        => new("Display name", null, 0u, 1_000, 1_000, 100, own, true, true);
}

/// <summary>The hero's verbs, built ONCE by the host (a stable instance): share the link, the ⋯ menu and a stat that links into its list route.</summary>
public sealed record ProfileHeroActs(Action Share, Func<ContextMenuModel?> Menu, Action<ProfileStat> OpenStat);

/// <summary>One section the page renders, in the plan's order: its body verdict, its cards (only for <see cref="ProfileSectionBody.Cards"/>), the "See all"
/// verb (the two people sections, only when the list holds more than the shelf shows), the section's own Retry, the click of its pivot tab and the callback that
/// registers its anchor node (`OnRealized`) with the host's scroll spy.</summary>
public sealed record ProfileSectionSlot(ProfileSection Section, ProfileSectionBody Body, ProfileCard[] Cards, Action? SeeAll, Action Retry,
                                        Action Click, Action<NodeHandle> Anchor);

/// <summary>The props of <see cref="ViewKeys.ProfilePage"/>. The host rebuilds it on every render it makes (the tables, the accent, the hero width and the
/// band's engaged edge are read there, as the page always did), so the view re-runs exactly when the page tree used to be rebuilt; it never compares equal to
/// another instance.</summary>
public sealed record ProfilePageProps
{
    /// <inheritdoc />
    public bool Equals(ProfilePageProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    // ── identity ──

    /// <summary>The route's key (the keys of the page, its scroller, the tint leaf and the wash derive from it).</summary>
    public required string RouteKey { get; init; }

    /// <summary>The entity scope epoch as text: the page body is a keyed child, so a scope switch remounts the sections and their anchors register again.</summary>
    public required string ScopeKey { get; init; }

    /// <summary>The scroll key of the page's offset (the tab's scope plus the route key).</summary>
    public required string ScrollKey { get; init; }

    /// <summary>The person's uri text (empty for an invalid route); the wash, the Follow toggles and the ⋯ key on it.</summary>
    public required string Uri { get; init; }

    /// <summary>The person's display name (empty for an invalid route).</summary>
    public required string Name { get; init; }

    /// <summary>Your own profile: no Follow, the "Open account" button, the top-artists section.</summary>
    public required bool Own { get; init; }

    // ── state ──

    /// <summary>Pending: the header, the measured width or the reveal have not settled, so the derived skeleton shows.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The header failed: the error vacancy shows.</summary>
    public required Func<bool> Failed { get; init; }

    /// <summary>The error vacancy's Retry; null while the route holds no valid person.</summary>
    public Action? Retry { get; init; }

    /// <summary>The 404 face (a private or closed account): the page draws its "isn't available" vacancy as content, no Retry.</summary>
    public required bool Unavailable { get; init; }

    /// <summary>True once the page body may be composed (the host composes it only then, exactly as the page always did).</summary>
    public required bool BodyReady { get; init; }

    // ── geometry ──

    /// <summary>The measured page width (the default Wide width until the first measure).</summary>
    public required float Width { get; init; }

    /// <summary>The ONE resolved (hysteretic) hero metrics of this render: the hero, the band's gutter, the magazine's gutter and the wash all read it.</summary>
    public required ProfileHeroMetrics Metrics { get; init; }

    /// <summary>The page's size observer: writes the measured width (a 0.5-DIP floor) on the host.</summary>
    public required Action<RectF> Measure { get; init; }

    // ── colour ──

    /// <summary>The watched accent, read once per render.</summary>
    public required ColorF Accent { get; init; }

    /// <summary>The accent as a live read, for the leaves that bind it (the pivot).</summary>
    public required Func<ColorF> AccentFn { get; init; }

    /// <summary>The accent as an UNTRACKED read, for the derived skeleton (it runs outside any render and must not subscribe anything).</summary>
    public required Func<ColorF> AccentPeek { get; init; }

    /// <summary>The page accent the page PROVIDES to its subtree (`Design.AccentCtx`): the heart and every other ambient consumer read it.</summary>
    public required IReadSignal<Design.PageAccent>? PageAccent { get; init; }

    /// <summary>The avatar url the wash is graded from (null when there is no gradeable avatar).</summary>
    public string? PaletteUrl { get; init; }

    /// <summary>The person's own colour as the wash's payload accent (0 when a gradeable avatar leads).</summary>
    public required uint PayloadAccent { get; init; }

    /// <summary>Colour washes are on (`appearance.colorWashes.enabled`): off, the blend wash renders nothing.</summary>
    public required bool Washes { get; init; }

    /// <summary>The shell-tint claim (`Palette.ShellTint`): a zero-size leaf the page keeps in its tree so the window chrome follows the page.</summary>
    public required Element Tint { get; init; }

    // ── the hero ──

    /// <summary>What the hero states (the loaded person's facts).</summary>
    public required ProfileHeroText Hero { get; init; }

    /// <summary>The hero's verbs (share, ⋯, a linking stat).</summary>
    public required ProfileHeroActs HeroActs { get; init; }

    // ── the band and the spy ──

    /// <summary>The band is past the sentinel's sticky edge: it takes input and the hero's expanded copy stops hitting.</summary>
    public required bool Compact { get; init; }

    /// <summary>The sentinel's engaged edge (`Sticky(.., engaged:)`): the ENGINE writes it on the flip, so the view only hands it on.</summary>
    public required Func<Signal<bool>> CompactEdge { get; init; }

    /// <summary>The lit tab (the spy's answer), bound by the pivot.</summary>
    public required IReadSignal<int> Active { get; init; }

    /// <summary>The page viewport's scroll handle: the spy projects its geometry off it and the band title scrolls it home.</summary>
    public required ScrollHandle Scroll { get; init; }

    /// <summary>The scroll viewport's node, reported once realized (the spy resolves against it).</summary>
    public required Action<NodeHandle> CaptureViewport { get; init; }

    /// <summary>The scroll offset, provided to the sections (`LazyScroll`) so they window against it.</summary>
    public required IReadSignal<float> ScrollY { get; init; }

    /// <summary>The band title's click: back to the top of the page.</summary>
    public required Action ScrollToTop { get; init; }

    // ── the sections ──

    /// <summary>The sections in the plan's order (<see cref="ProfileSections.Plan"/>); each is also one tab of the band's pivot.</summary>
    public required IReadOnlyList<ProfileSectionSlot> Sections { get; init; }
}
