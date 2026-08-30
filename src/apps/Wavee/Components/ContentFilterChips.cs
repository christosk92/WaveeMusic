using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The Liked Songs content-filter chip bar — Spotify's own descriptor concepts ("K-Pop", "Chill", "Energetic")
/// as a one-tap lens over the list.
///
/// Spotify's curated set from <c>content-filter/v1/liked-songs</c> is the primary source; chips DERIVED from the tracks
/// in view (extension kind 6 already carries each descriptor's presentation name in the row bundle this list fetches
/// anyway) are the fallback for offline / a 404 account / a shape change.
///
/// A chip can never filter the list to nothing, but that is enforced by AVAILABILITY, not by membership: the curated
/// set is library-scoped and routinely names concepts whose rows have not been enriched yet, so dropping those hid the
/// whole bar on a cold list. They are shown and disabled instead, and become live as enrichment lands.
///
/// Selection is EXCLUSIVE (All + at most one chip). These are a lens, not accumulating constraints — two genres ANDed
/// together almost always yields nothing, and users read a second tap as "switch", not "narrow".</summary>
static class ContentFilterChips
{
    /// <summary>The chip set for a track list — see <see cref="ContentFilterTags.Derive"/>, which owns the rule and is
    /// unit-tested. Kept here as the component's entry point so call sites read naturally. Everything derived is
    /// evidenced by construction (it came FROM the tracks), so the whole set is selectable.</summary>
    public static ContentFilterChipSet Derive(IReadOnlyList<Track> tracks)
    {
        var titles = ContentFilterTags.Derive(tracks);
        return new ContentFilterChipSet(titles, titles.Count);
    }

    /// <summary>Chip height, matching <c>ConcertUi.FilterToken</c> so the app's two filter rails line up.</summary>
    const float ChipHeight = 32f;
    /// <summary>Rail height: the chip plus room for its focus visual, which draws outside the chip's own box.</summary>
    const float RailHeight = 40f;
    /// <summary>Total vertical space contributed to stacked detail chrome, including its bottom semantic gap.</summary>
    public static float VerticalExtent => RailHeight + Spacing.S;

    /// <summary>The bar. Returns null when there is nothing to offer, so the caller can omit the row entirely.
    ///
    /// ONE line that scrolls, never a wrapped block. A curated Liked set runs to 15+ concepts, which wrapped into a
    /// second and third row and pushed the tracklist down the page. <paramref name="scrollKey"/> scopes the horizontal
    /// offset so each list remembers its own position and a navigation does not inherit the previous one.</summary>
    public static Element? Build(ContentFilterChipSet chips, string? selected, Action<string?> select, string allLabel,
                                 string scrollKey)
    {
        if (chips.Count == 0) return null;

        var children = new List<Element>(chips.Count + 1) { Chip(allLabel, selected is null, true, () => select(null)) };
        for (int i = 0; i < chips.Count; i++)
        {
            string tag = chips.Titles[i];
            bool on = string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase);
            // A chip with no evidence in the rows can only filter to an empty list, so it is shown (the server's
            // curated set stays complete, which is what makes the bar appear on a cold library) but not tappable.
            // It becomes live the moment descriptor enrichment lands for those rows and the set recomputes.
            bool available = chips.IsEvidenced(i);
            // Re-tapping the active chip clears it: the chip IS the toggle, so there is no dead tap.
            children.Add(Chip(tag, on, available, available ? () => select(on ? null : tag) : null));
        }

        // The edge fade IS the overflow affordance — offset-driven and live per frame, so it says "more this way"
        // without adding chrome to a row that is already dense. Same construction as the Concerts filter rail.
        return ScrollView(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = children.ToArray(),
        }, horizontal: true) with
        {
            Grow = 0f, Height = RailHeight, AutoEdgeFade = true, SuppressScrollBar = true,
            Margin = new Edges4(0f, 0f, 0f, Spacing.S),
            ScrollKey = scrollKey,
        };
    }

    static Element Chip(string label, bool selected, bool available, Action? onClick) => new BoxEl
    {
        Role = AutomationRole.Button, Focusable = available, Cursor = available ? CursorId.Hand : CursorId.Arrow,
        IsEnabled = available,
        FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
        // Shrink 0 is load-bearing on a non-wrapping row: without it flex compresses every pill to fit the viewport
        // and the labels ellipsise instead of the rail overflowing, which is the opposite of what the scroller is for.
        Height = ChipHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
        Corners = CornerRadius4.All(999f),
        Fill = selected ? Tok.AccentDefault : Tok.FillControlDefault,
        HoverFill = !available ? Tok.FillControlDefault : selected ? Tok.AccentSecondary : Tok.FillControlSecondary,
        BorderWidth = 1f,
        BorderColor = selected ? ColorF.Transparent : Tok.StrokeControlDefault,
        HoverBorderColor = !available ? Tok.StrokeControlDefault : selected ? ColorF.Transparent : Tok.AccentDefault,
        HoverScale = WaveeMotion.ScaleSubtle.HoverIf(available),
        HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
        PressScale = WaveeMotion.ScaleSubtle.PressIf(available),
        OnClick = onClick,
        Children =
        [
            new TextEl(label)
            {
                Size = 13f, Weight = (ushort)(selected ? 600 : 400), MaxLines = 1,
                Color = selected ? Tok.TextOnAccentPrimary : available ? Tok.TextPrimary : Tok.TextDisabled,
            },
        ],
    };
}
