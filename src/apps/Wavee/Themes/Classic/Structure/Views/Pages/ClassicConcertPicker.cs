// ── Themes/Classic/Structure/Views/Pages/ClassicConcertPicker.cs — Classic's `Concert.PlacePicker` (themes plan 6.1, T10b3) ───────────────────────
//
// From `Entities\Concert.UI.cs` §7 (`PlacePickerPanel.Render` and `PlaceRow`; 0.2.9 `ConcertLocationPickerPanel`): 360 wide: the 340×32 search field, "Use my
// location" (Standard; "Locating…" and disabled while loading — it FILLS the results, it never saves), the critical error line (wrap ≤ 3), then the results
// (MaxH 248) or one 48-high "Searching…" / "No locations found" row. The debounced search, the locate flow and the save are `Concert.PlacePickerHost`'s
// (core); the view reads the live signals and calls its two verbs.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Classic.ClassicConcertRules;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's concert location picker.</summary>
internal static class ClassicConcertPicker
{
    /// <summary>The registered template of <see cref="ViewKeys.ConcertPlacePicker"/>.</summary>
    public static Element Template(ConcertPlacePickerProps props, in ViewContext ctx)
    {
        var results = props.Results.Value;
        bool loading = props.Loading.Value;
        string? error = props.Error.Value;
        var rows = new Element[results.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            int slot = results[i];
            rows[i] = PlaceRow(slot, () => props.Select(slot));
        }
        var query = props.Query;
        var kids = new List<Element>(4)
        {
            Embed.Comp(() => new EditableText
            {
                Placeholder = Loc.Get(Strings.Concerts.Location.SearchCities), Width = Rules.PickerFieldW, Height = Rules.PickerFieldH, Text = query(),
            }),
            Button.Standard(Loc.Get(loading ? Strings.Concerts.Location.Locating : Strings.Concerts.Location.UseMine),
                props.UseMyLocation, isEnabled: !loading),
        };
        if (!string.IsNullOrWhiteSpace(error))
            kids.Add(Body(error) with { Color = Tok.SystemFillCritical, Wrap = TextWrap.Wrap, MaxLines = 3 });
        kids.Add(rows.Length > 0
            ? new ScrollEl { ContentSized = true, MaxHeight = Rules.PickerResultsMaxH, Content = new BoxEl { Direction = 1, Gap = Rules.FlyoutGap, Children = rows } }
            : new BoxEl
            {
                Height = Rules.PickerRowH, AlignItems = FlexAlign.Center, Direction = 0,
                Children = [R.Type.TrackMeta(Loc.Get(loading ? Strings.Concerts.Location.Searching : Strings.Concerts.Location.NoneFound))],
            });
        return new BoxEl { Direction = 1, Width = Rules.PickerW, Gap = Spacing.S, Padding = Edges4.All(Spacing.M), Children = kids.ToArray() };
    }

    static Element PlaceRow(int slot, Action onClick)
    {
        var place = ConcertPlaces.From(slot);
        string name = place?.Name ?? "";
        string detail = place is null ? ""
            : !string.IsNullOrWhiteSpace(place.Region) && !string.IsNullOrWhiteSpace(place.Country) ? place.Region + " - " + place.Country
            : place.Region ?? place.Country ?? "";
        return new BoxEl
        {
            Key = "place:" + slot.ToString(CultureInfo.InvariantCulture),
            Direction = 0, MinHeight = Rules.PickerRowH, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                Icon(Icons.MapPin, Rules.PickerPin, Tok.TextSecondary) with { Shrink = 0f },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 1f,
                    Children = detail.Length > 0
                        ? [R.Type.TrackTitle(name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, R.Type.TrackMeta(detail) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                        : [R.Type.TrackTitle(name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }
}
