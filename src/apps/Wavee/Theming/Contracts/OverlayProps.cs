// ── Theming/Contracts/OverlayProps.cs — the props of the overlay keys (themes plan 6.0, T11a) ──────────────────────────────────
//
// Each record is what ONE render of the picture needs; the core host builds it, mounts `Slots.View(key, props)` and keeps the state, the
// overlay handles and the verbs. WVTH006: sealed records, init-only members, no writable `Signal<T>`.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.OverlayProfileChip"/>.</summary>
public sealed record ProfileChipProps
{
    /// <summary>The account's identity is known (false: the skeleton chip).</summary>
    public required bool Known { get; init; }

    /// <summary>The display name ("" while presenting).</summary>
    public required string Name { get; init; }

    /// <summary>The avatar's url, or null.</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>The chrome ladder's named form (the name beside the avatar) is on.</summary>
    public required bool ShowName { get; init; }

    /// <summary>Presentation mode (a listing image): the avatar alone, no name.</summary>
    public required bool Presenting { get; init; }

    /// <summary>The name caption's hard cap (<c>Actions.ProfileRules.NameCap</c>).</summary>
    public required float NameCap { get; init; }

    /// <summary>Open (or close) the account menu.</summary>
    public required Action Open { get; init; }

    /// <summary>Report the chip's node: the menu anchors to it.</summary>
    public required Action<NodeHandle> Realized { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.OverlayProfileMenu"/>.</summary>
public sealed record ProfileMenuProps
{
    /// <summary>The account's identity is known (false: quiet bars at the header's height).</summary>
    public required bool Known { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>The avatar's url, or null.</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>The tier is known and gets a line (an unknown tier prints nothing).</summary>
    public required bool ShowsTier { get; init; }

    /// <summary>The tier is Premium (a star and the premium ink), else the plain Free badge.</summary>
    public required bool Premium { get; init; }

    /// <summary>The rows, in the row table's order (the core decided which exist; every row closes the menu first, then acts).</summary>
    public required IReadOnlyList<MenuFlyoutItem> Items { get; init; }

    /// <summary>Close the menu.</summary>
    public required Action Close { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.OverlayRuntimeBanner"/>.</summary>
public sealed record RuntimeBannerProps
{
    /// <summary>The banner is up (false: a bare zero-footprint box; it floats and never reflows the page).</summary>
    public required bool Show { get; init; }

    /// <summary>The body line for the runtime's issue (resolved text).</summary>
    public required string Message { get; init; }

    /// <summary>Dismiss the banner (its close button).</summary>
    public required Action Dismiss { get; init; }

    /// <summary>Open the runtime setup card (the "Set up" button).</summary>
    public required Action SetUp { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.OverlayToast"/>.</summary>
public sealed record ToastCardProps
{
    /// <summary>The card's title.</summary>
    public required string Title { get; init; }

    /// <summary>The download's fraction: the bar is BOUND to it, so a progress tick is a float write, not a reconcile.</summary>
    public required FloatSignal Progress { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.OverlayOfflineStrip"/>.</summary>
public sealed record OfflineStripProps
{
    /// <summary>The notice (null: the localized "You're offline · showing saved content.").</summary>
    public string? Message { get; init; }

    /// <summary>Retry (null: no button).</summary>
    public Action? OnRetry { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.OverlayOfflineVacancy"/> (contract 1.8, tuned Classic X1): a page that has nothing saved to show
/// while the session is offline.</summary>
public sealed record OfflineVacancyProps
{
    /// <summary>The page's own name ("Browse"), for "Browse needs a connection." Empty: the generic sentence.</summary>
    public string Page { get; init; } = "";

    /// <summary>Retry: reconnect and load the page again (null: no button).</summary>
    public Action? OnRetry { get; init; }

    /// <summary>Go to the saved library, which works offline (null: no link).</summary>
    public Action? GoToLibrary { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.MenuContext"/>.</summary>
/// <param name="ArtUrl">The art's url.</param>
/// <param name="Circular">A circle (an artist) rather than a rounded square.</param>
public sealed record ContextMenuTileProps(string ArtUrl, bool Circular);

/// <summary>The props of <see cref="ViewKeys.DialogLogoutConfirm"/>.</summary>
public sealed record LogoutConfirmProps
{
    /// <summary>Cancel (closes the card).</summary>
    public required Action Cancel { get; init; }

    /// <summary>Log out (closes the card, then signs out).</summary>
    public required Action Confirm { get; init; }
}
