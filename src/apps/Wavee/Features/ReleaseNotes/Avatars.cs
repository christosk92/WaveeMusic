using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core.ReleaseNotes;

namespace Wavee;

/// <summary>The overlapping "who did this" stack at the end of a changelog line.
///
/// <para>Deliberately INITIALS, not GitHub avatar images: an avatar is a third-party image request per contributor per
/// release, on a page that already spends its network budget on the notes document and the issue states — and the
/// initials read at 18 DIP where a 18px photo does not. The login is the tooltip, which is the answer the reader
/// actually wants.</para></summary>
static class Avatars
{
    const float Size = 18f;
    const int Max = 4;

    // A fixed, deterministic palette — the same login always gets the same tint, so a contributor is recognisable
    // down a page without anyone storing a colour.
    static ColorF Tint(string login)
    {
        int h = 0;
        for (int i = 0; i < login.Length; i++) h = (h * 31 + login[i]) & 0x7fffffff;
        return (h % 6) switch
        {
            0 => ColorF.FromRgba(0x4A, 0x7A, 0xC0, 0xFF),
            1 => ColorF.FromRgba(0xC0, 0x7A, 0x4A, 0xFF),
            2 => ColorF.FromRgba(0x4A, 0xA8, 0x7A, 0xFF),
            3 => ColorF.FromRgba(0x8A, 0x6A, 0xC0, 0xFF),
            4 => ColorF.FromRgba(0xC0, 0x5A, 0x7A, 0xFF),
            _ => ColorF.FromRgba(0x5A, 0x8A, 0x8A, 0xFF),
        };
    }

    static string Initials(string login)
    {
        if (string.IsNullOrEmpty(login)) return "?";
        // "christosk92" → "C"; "jane-doe" → "JD". Two letters at most; the tooltip carries the rest.
        int dash = login.IndexOfAny(['-', '_', '.']);
        if (dash > 0 && dash + 1 < login.Length)
            return string.Concat(char.ToUpperInvariant(login[0]).ToString(), char.ToUpperInvariant(login[dash + 1]).ToString());
        return char.ToUpperInvariant(login[0]).ToString();
    }

    /// <summary>The stack. Returns an empty (zero-size, hit-invisible) box when there is nobody to show, so a caller
    /// can add it unconditionally.</summary>
    public static Element Create(ReleaseContributor[]? contributors)
    {
        if (contributors is null || contributors.Length == 0)
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

        int shown = contributors.Length > Max ? Max : contributors.Length;
        var kids = new List<Element>(shown + 1);
        for (int i = 0; i < shown; i++)
        {
            var c = contributors[i];
            string login = c.Login ?? "";
            kids.Add(ToolTip.Wrap(new BoxEl
            {
                Width = Size, Height = Size, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(Size / 2f), Fill = Tint(login),
                BorderWidth = 1.5f, BorderColor = Tok.FillSolidBase,
                // Overlap the previous disc by 5 DIP, the prototype's stacked-avatar tell.
                Margin = i == 0 ? default : new Edges4(-5f, 0f, 0f, 0f),
                Children = [ new TextEl(Initials(login)) { Size = 9f, Weight = 700, Color = Tok.TextOnAccentPrimary } ],
            }, c.FirstTime ? Strings.WhatsNew.FirstContribution(login) : login));
        }
        if (contributors.Length > shown)
            kids.Add(new TextEl("+" + (contributors.Length - shown).ToString(CultureInfo.InvariantCulture))
                { Size = 10f, Weight = 600, Color = Tok.TextTertiary, Margin = new Edges4(4f, 0f, 0f, 0f) });

        return new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children = kids.ToArray(),
        };
    }
}
