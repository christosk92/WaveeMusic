// ── Screens/ColourEditor.cs — the custom colours editor's core card (#184, plan 3.8 "The custom editor") ───────────────────────
//
// A bare modal card (no light dismiss) over the active theme's `Dialog.ColourEditor` body, in two uses:
//   · MY OWN SET for a mode (`Open`): the six editable seeds (`CustomColours`), opened from my stored set, else the set on screen, else the
//     mode's first set; Start from copies a built-in's seeds;
//   · MY OWN ACCENT (`OpenOwnAccent`): one well, the accent, over the set on screen; Start from offers that set's named accents.
// The core holds every decision: the DRAFT, the EDITED WELL and the picker's colour cell. A pick writes the draft and re-solves it
// (`ColourSolver`); the body reads the solved draft through binds, so a drag repaints the wells and the preview without re-rendering the card,
// rebuilding a definition or persisting. The NOTE says which seeds the floors moved (`ColourSolver.Explain`). Reset puts back what the card
// opened with, Cancel closes, Apply is the only write (it stores and applies, live or on the transaction, then closes). Nothing outside the
// card changes before Apply.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Colours;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>Opens the custom colours editor (see the file header).</summary>
public static class ColourEditor
{
    private static readonly CustomSeed[] AllSeeds =
        [CustomSeed.Ground, CustomSeed.Surface, CustomSeed.Ink, CustomSeed.Muted, CustomSeed.Accent, CustomSeed.Surface2];

    private static readonly CustomSeed[] AccentOnly = [CustomSeed.Accent];

    /// <summary>Opens the editor of <paramref name="kind"/>'s own set over <paramref name="overlay"/>. A no-op without one.</summary>
    public static void Open(IOverlayService? overlay, ThemeKind kind)
    {
        var specs = ColourCatalog.For(kind);
        var starts = new ColourSeeds[specs.Length];
        var words = new string[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            starts[i] = CustomColours.From(specs[i]);
            words[i] = specs[i].DisplayName;
        }
        Show(overlay, new Session(kind, ColourChoice.EditorSeeds(kind), AllSeeds,
            Loc.Get(kind == ThemeKind.Light ? Strings.Settings.Appearance.ColoursEditorTitleLight : Strings.Settings.Appearance.ColoursEditorTitleDark),
            words, starts, seeds => ColourChoice.SetCustom(kind, seeds)));
    }

    /// <summary>Opens the editor of my own accent over the set on screen. A no-op without an overlay or without a set on screen.</summary>
    public static void OpenOwnAccent(IOverlayService? overlay)
    {
        ThemeKind kind = Tok.Theme;
        if (ColourChoice.SpecOf(kind) is not { } spec) return;
        ColourSeeds seeds = CustomColours.From(spec) with { Accent = ColourChoice.OwnAccent ?? spec.Seeds.Accent };
        var words = new string[spec.Accents.Length];
        var starts = new ColourSeeds[spec.Accents.Length];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = spec.Accents[i].Name;
            starts[i] = seeds with { Accent = spec.Accents[i].Color };
        }
        Show(overlay, new Session(kind, seeds, AccentOnly, Loc.Get(Strings.Settings.Appearance.ColoursEditorTitleAccent), words, starts,
            s =>
            {
                ColourChoice.SetOwnAccent(s.Accent);
                ColourChoice.SetAccentSource(AccentSource.Own);
                return true;
            }));
    }

    private static void Show(IOverlayService? overlay, Session session)
    {
        if (overlay is null) return;
        OverlayHandle? h = null;
        h = overlay.Open(
            () => NodeHandle.Null,
            () => Embed.Comp(() => new ColourEditorCore(session, () => h?.Close())),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
    }

    /// <summary>One opening of the card: open-time constants only.</summary>
    private sealed record Session(ThemeKind Kind, ColourSeeds Opened, CustomSeed[] Editable, string Title, string[] StartWords,
        ColourSeeds[] Starts, Func<ColourSeeds, bool> Apply);

    /// <summary>The draft as the body sees it: the edited wells' colours and words, the preview's rungs and the note.</summary>
    private static ColourEditorDraft Solve(ThemeKind kind, in ColourSeeds seeds, CustomSeed[] editable)
    {
        var spec = new ColourSpec(CustomColours.IdOf(kind), "", "", kind, seeds, System.Collections.Immutable.ImmutableArray<NamedAccent>.Empty);
        ColourLadder l = ColourSolver.Resolve(spec, seeds.Accent);
        var wells = new ColorF[editable.Length];
        var hex = new string[editable.Length];
        for (int i = 0; i < wells.Length; i++)
        {
            wells[i] = CustomColours.Get(seeds, editable[i], l);
            hex[i] = editable[i] == CustomSeed.Surface2 && seeds.Well is null
                ? Loc.Get(Strings.Settings.Appearance.ColoursEditorAuto)
                : "#" + wells[i].ToHex();
        }
        var moved = new List<string>(3);
        foreach (CustomSeed s in ColourSolver.Explain(spec, seeds.Accent))
            if (Array.IndexOf(editable, s) >= 0) moved.Add(Loc.Get(LabelKey(s)));
        string note = moved.Count == 0 ? "" : Strings.Settings.Appearance.ColoursEditorNote(string.Join(", ", moved));
        return new ColourEditorDraft(wells, hex, l.Ground, l.Well, l.Raised, l.Ink, l.InkSecondary, l.InkTertiary,
            l.AccentFill, l.AccentInk, l.OnAccent, l.WashInk with { A = 0.12f }, note);
    }

    /// <summary>The label key of <paramref name="seed"/>'s well.</summary>
    public static string LabelKey(CustomSeed seed) => seed switch
    {
        CustomSeed.Ground => Strings.Settings.Appearance.ColoursEditorGround,
        CustomSeed.Surface => Strings.Settings.Appearance.ColoursEditorSurface,
        CustomSeed.Ink => Strings.Settings.Appearance.ColoursEditorInk,
        CustomSeed.Muted => Strings.Settings.Appearance.ColoursEditorMuted,
        CustomSeed.Accent => Strings.Settings.Appearance.ColoursEditorAccent,
        _ => Strings.Settings.Appearance.ColoursEditorSurface2,
    };

    /// <summary>The card. Its session is open-time constants; everything that changes while it is open is its own signals.</summary>
    [CoreHost]
    sealed class ColourEditorCore(Session session, Action close) : Component
    {
        public override Element Render()
        {
            var s = session;
            var seeds = UseSignal(s.Opened);
            var selected = UseSignal(0);
            var pickerEpoch = UseSignal(0);
            var draft = UseSignal(Solve(s.Kind, s.Opened, s.Editable));
            var picker = UseSignal(draft.Peek().Wells[0]);

            void Commit(in ColourSeeds next)
            {
                seeds.Value = next;
                draft.Value = Solve(s.Kind, next, s.Editable);
            }

            void Reseed()
            {
                picker.Value = draft.Peek().Wells[selected.Peek()];
                pickerEpoch.Value = pickerEpoch.Peek() + 1;
            }

            var labels = new string[s.Editable.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(LabelKey(s.Editable[i]));

            return ViewSlots.View(ViewKeys.DialogColourEditor, new ColourEditorProps
            {
                Title = s.Title,
                WellLabels = labels,
                Selected = selected,
                Select = i =>
                {
                    if ((uint)i >= (uint)s.Editable.Length || i == selected.Peek()) return;
                    selected.Value = i;
                    Reseed();
                },
                Draft = draft,
                Picker = () => picker,
                PickerEpoch = pickerEpoch,
                Pick = c =>
                {
                    // A ground the mode refuses (a light page in a dark set) is not taken: the solver's polarity rule, kept visible as the
                    // unchanged well rather than a broken preview.
                    var next = CustomColours.With(seeds.Peek(), s.Editable[selected.Peek()], c);
                    if (!ColourSolver.Fits(s.Kind, next.Ground)) return;
                    Commit(next);
                },
                StartFromChoices = s.StartWords,
                StartFrom = i =>
                {
                    if ((uint)i >= (uint)s.Starts.Length) return;
                    Commit(s.Starts[i]);
                    Reseed();
                },
                Reset = () =>
                {
                    Commit(s.Opened);
                    Reseed();
                },
                Cancel = close,
                Apply = () =>
                {
                    // Close first, then apply: the card's nodes leave before the re-theme cross-fade starts, so none of them is left holding a
                    // brush transition (the seeds were validated as they were picked, so the apply cannot refuse them).
                    ColourSeeds picked = seeds.Peek();
                    close();
                    s.Apply(picked);
                },
            });
        }
    }
}
