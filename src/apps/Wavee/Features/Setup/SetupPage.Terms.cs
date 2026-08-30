using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 1 · Terms &amp; privacy (<c>data-step="1"</c>). Right column: a "what Wavee needs" trio of
/// <see cref="SetupCompact.InfoCard"/>s over trademark fine print, pinned to the floor at Wide (no scroll — the
/// column fits its own budget). Left stage: ONE card in two states — a real summary of the agreement (header +
/// four numbered sections with a one-line teaser each + a link footer) that GROWS INTO the scrollable document in
/// place, never a Flyout (floats above the plate and fights the modal's own Escape ordering) nor a TeachingTip
/// (336×520 cap, fixed 14-px body, wrong semantics; see the work-package plan). Below Wide there is no stage
/// column, so the same link + document swap is appended inline to the (already scrollable) body instead.</summary>
sealed class SetupTermsPage : Component
{
    /// <summary>The revision of the agreement THIS page presents. Accepting writes it to
    /// <c>WaveeSettings.TermsAcceptedVersion</c> (<see cref="SetupSession.Primary"/>), and a launch that finds an older
    /// recorded acceptance re-arms the wizard (<see cref="SetupGating.NeedsTermsRearm"/>). Bump it in ONE place —
    /// <see cref="SetupGating.TermsVersion"/>, which this aliases — whenever the four sections below change materially;
    /// the constant lives over there because the gate and <c>SetupBootstrap</c> are engine-free and source-included by
    /// the test assembly, which cannot see this component at all.</summary>
    public const int CurrentVersion = SetupGating.TermsVersion;

    /// <summary>Wavee's own privacy statement — deliberately a link to the repository's <c>PRIVACY.md</c> rather than a
    /// fifth agreement section: it describes what the APP does with local data, which is a different document from the
    /// terms the user is accepting here, and it must stay readable/greppable outside the running app.</summary>
    const string PrivacyUrl = "https://github.com/christosk92/WaveeMusic/blob/main/PRIVACY.md";

    // ── The summary/document swap's motion, declared once (the engine's declarative Enter/Exit terminals, NOT a
    //    hook branch — reduced motion is resolved by the animation slab as a VALUE, so there is nothing to test
    //    here). Enter rises 12 DIP out of the card's own footprint; exit lifts 8 and fades, so the two states cross
    //    in the SAME direction the disclosure reads in. ──────────────────────────────────────────────────────────
    static readonly EnterExit Rise = new(Dy: 12f, Opacity: 0f, Active: true);
    static readonly EnterExit Lift = new(Dy: -8f, Opacity: 0f, Active: true);

    /// <summary>The accent number chip's diameter — a 22-DIP circle, big enough to read a digit at 11 px and small
    /// enough that a summary row stays 34 DIP (title 18 + teaser 16) tall.</summary>
    const float ChipSize = 22f;

    public override Element Render()
    {
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        bool wide = SetupLayout.ShowsHero(tierSig.Value);

        var agreementOpen = UseSignal(false);
        var post = UsePost();
        void OpenPrivacy() => LoginView.OpenUrl(PrivacyUrl);

        // Escape never reaches the document card on its own: the wizard is an input-blocking modal, and OverlayHost's
        // key preview hands Escape to the top modal BEFORE focus routing runs (its first-run close is then vetoed, so
        // the key simply dies). The plate's close veto is the one hook that runs first, so while the agreement is open
        // it is registered on the session as the thing that Escape closes (SetupDialog → SetupGating.EscapeClosesPlate).
        // The card's own OnKeyDown below stays for the inline (non-modal-hosted) tiers and for parity.
        // Registered from the intents themselves (not an effect over the signal) so Render keeps its fine-grained
        // Flow.Show subscription instead of re-rendering the whole page on every toggle; the mount effect's cleanup
        // is the unmount backstop (Decline / Accept with the agreement still open).
        // Cleared unconditionally (no delegate-identity check): local-function delegates are minted per render, so
        // "is it still mine?" would compare two different closures; and only ONE page is mounted at a time, so the
        // consumer can only ever be this page's.
        void Open() { agreementOpen.Value = true; if (SetupSession.Current is { } s) s.EscapeConsumer = Close; }
        void Close() { agreementOpen.Value = false; ReleaseEscape(); }
        static void ReleaseEscape() { if (SetupSession.Current is { } s) s.EscapeConsumer = null; }
        UseEffect(() => ReleaseEscape);

        // Focus follows the disclosure: the document card is Focusable and owns Escape, so keyboard users must be
        // INSIDE it the moment it appears or Escape lands on whatever had focus before. The move is POSTed rather
        // than called from the realize callback — the same marshalling DetailTracks.CaptureSearchButton uses, so
        // the dispatcher never re-enters focus routing in the middle of the reconcile that created the node.
        void FocusDoc(NodeHandle node) => post(() => InputHooks.Current.Default.FocusNode?.Invoke(node, false));

        // Two links, one row: the agreement (opens IN PLACE, see AgreementDoc) and the privacy statement (leaves for the
        // browser). Side by side because a user asked to accept has exactly two things they might want to read first,
        // and burying one of them under the other is how "I never saw a privacy policy" happens. Below Wide only —
        // at Wide these two live in the summary card's own footer instead.
        Element LinkRow() => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Center, Shrink = 0f,
            Children =
            [
                HyperlinkButton.Create(Loc.Get(Strings.Setup.Terms.ReadFull) + " · " + Strings.Setup.Terms.SectionsCount(4), Open),
                new BoxEl { Width = 1f, Height = 12f, Fill = Tok.StrokeDividerDefault },
                HyperlinkButton.Create(Loc.Get(Strings.Setup.Terms.PrivacyLink), OpenPrivacy),
            ],
        };

        Element[] needCards =
        [
            SetupCompact.SectionLabel(Loc.Get(Strings.Setup.Terms.NeedGroup)),
            SetupCompact.InfoCard(Icons.Contact, Loc.Get(Strings.Setup.Terms.PremiumTitle), Loc.Get(Strings.Setup.Terms.PremiumBody)),
            SetupCompact.InfoCard(Icons.Download, Loc.Get(Strings.Setup.Terms.RuntimeTitle), Loc.Get(Strings.Setup.Terms.RuntimeBody)),
            SetupCompact.InfoCard(Icons.Folder, Loc.Get(Strings.Setup.Terms.DataTitle), Loc.Get(Strings.Setup.Terms.DataBody)),
        ];
        Element fine = SetupCompact.FinePrint(Loc.Get(Strings.Setup.Terms.Fine), maxLines: 5);

        Element? stage = wide
            // ── THE OVERLAP FIX ────────────────────────────────────────────────────────────────────────────────
            // BOTH states live INSIDE one SetupStage.Column, so the stage is the fixed 344-DIP plate in every
            // state. The open state used to hand SetupPageHost.Frame a bare `Grow = 1f` BoxEl as its whole stage
            // slot: with no Width and no Shrink = 0 it took whatever the frame's row would give it and painted
            // straight over the decision column's own header ("Before we start / Terms & what Wavee is"). A
            // column-shaped stage cannot do that by construction — the document scrolls INSIDE the 296-DIP inner
            // width instead of growing past it. Anything added here goes inside the Column, never beside it.
            ? SetupStage.Column(
                Flow.Show(() => agreementOpen.Value,
                    AgreementDoc(Close, fill: true, FocusDoc) with
                    {
                        Key = "terms:stage:doc", Enter = Rise, Exit = Lift, Transition = MotionTok.StandardEnter,
                    },
                    new BoxEl
                    {
                        Key = "terms:stage:summary",
                        Direction = 1, Gap = SetupLayout.StageGap, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                        AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch,
                        Enter = Rise, Exit = Lift, Transition = MotionTok.StandardEnter,
                        Children =
                        [
                            AgreementSummary(Open, OpenPrivacy),
                            SetupStage.Spacer(),
                            SetupStage.Caption(Loc.Get(Strings.Setup.Terms.StageCaptionTitle), Loc.Get(Strings.Setup.Terms.StageCaptionSub)),
                        ],
                    }))
                // Declarative FLIP on the stage plate itself: at the nominal 896×576 the column is already
                // full-height, so the swap is carried by the Enter/Exit terminals above and this is inert (the FLIP
                // deadband eats a zero-delta). It earns its keep on a resized plate, where the column's own bounds
                // do move and would otherwise snap between the two states.
                with { Layout = LayoutTransition.AutoAll }
            : null;

        Element body = wide
            // scrollBody:false → the frame gives this a Grow=1 slot instead of a ScrollEl viewport, so the Spacer
            // between the cards and the fine print can actually reach the floor (a ScrollEl has nothing for it to
            // grow into — SetupPageHost.cs's own reasoning for the flag).
            ? SetupCompact.Column([.. needCards, SetupCompact.Spacer(), fine]) with { Grow = 1f, Shrink = 1f, MinHeight = 0f }
            // No stage column here — the same link + in-place document swap rides along in the (always-scrollable)
            // body instead, so the full agreement is still one click away.
            : SetupCompact.Column([.. needCards, fine, LinkRow(),
                Flow.Show(() => agreementOpen.Value,
                    AgreementDoc(Close, fill: false, FocusDoc) with
                    {
                        Key = "terms:body:doc", Enter = Rise, Exit = Lift, Transition = MotionTok.StandardEnter,
                    })]);

        return SetupPageHost.Frame(SetupPage.Terms, Loc.Get(Strings.Setup.Eyebrow.Terms), Loc.Get(Strings.Setup.Terms.Title),
            body, lead: Loc.Get(Strings.Setup.Terms.Lead), leadMaxLines: 2, stage: stage, scrollBody: !wide);
    }

    // ── The agreement's own content, resolved per render ────────────────────────────────────────────────────────

    /// <summary>The four agreement sections for the CURRENT locale. Deliberately a method, not a cached static array:
    /// a locale switch re-renders the wizard, and a frozen array would keep serving the old language forever.</summary>
    static (string Title, string Body)[] Sections() =>
    [
        (Loc.Get(Strings.Setup.Terms.Section1Title), Loc.Get(Strings.Setup.Terms.Section1Body)),
        (Loc.Get(Strings.Setup.Terms.Section2Title), Loc.Get(Strings.Setup.Terms.Section2Body)),
        (Loc.Get(Strings.Setup.Terms.Section3Title), Loc.Get(Strings.Setup.Terms.Section3Body)),
        (Loc.Get(Strings.Setup.Terms.Section4Title), Loc.Get(Strings.Setup.Terms.Section4Body)),
    ];

    /// <summary>A section's one-line teaser for the summary card: its body up to and including the first sentence
    /// break. A body with no ". " in it falls back to the whole string — the row is <c>MaxLines = 1</c> with
    /// character ellipsis either way, so the worst case is a trimmed line, never a wrapped one.</summary>
    static string Teaser(string body)
    {
        int i = body.IndexOf(". ", StringComparison.Ordinal);
        return i > 0 ? body[..(i + 1)] : body;
    }

    // ── Shared card parts ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The accent-tinted number chip both states put in front of a section — 18 % accent wash under accent
    /// ink, which is the same "quiet affirmative" treatment <see cref="SetupStage.Pill"/> uses for its accent state.
    /// The number lives HERE, not in the section title string (the loc values carry no "1 · " prefix any more).</summary>
    static Element NumberChip(int index) => new BoxEl
    {
        Width = ChipSize, Height = ChipSize, Shrink = 0f,
        Corners = Radii.Circle(ChipSize), Fill = Tok.AccentDefault with { A = 0.18f },
        Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
        Children = [new TextEl((index + 1).ToString()) { Size = 11f, LineHeight = 14f, Weight = 600, Color = Tok.AccentTextPrimary }],
    };

    /// <summary>The header row both states share: the document glyph, the agreement's title, and one trailing slot —
    /// a "4 sections" meta pill while closed, the close button while open. Same row, same height, so the swap reads
    /// as one card changing state rather than two different cards.</summary>
    static Element CardHeader(Element trailing) => new BoxEl
    {
        Direction = 0, Gap = 8f, Shrink = 0f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
        Children =
        [
            Icon(Icons.Document, 16f, Tok.AccentTextPrimary),
            SetupStage.CardTitle(Loc.Get(Strings.Setup.Terms.AgreementTitle)) with { Grow = 1f, Basis = 0f, MinWidth = 0f },
            trailing,
        ],
    };

    /// <summary>The header's trailing meta pill while the card is closed ("4 sections").</summary>
    static Element MetaPill(string text) => new BoxEl
    {
        Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
        Corners = Radii.FullAll, Fill = Tok.FillControlDefault,
        Children =
        [
            new TextEl(text) { Size = 11f, LineHeight = 14f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    /// <summary>The 1-px rule between summary rows / document sections.</summary>
    static Element Hairline() => new BoxEl
    {
        Height = 1f, Shrink = 0f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault,
    };

    // ── Closed state: a real summary of the agreement ───────────────────────────────────────────────────────────

    /// <summary>One summary row: the accent number chip, the section title, and the first sentence of its body in
    /// tertiary ink, clamped to one line. The teaser is what turns the old grey skeleton bars into an actual
    /// preview — a reader can tell what section 3 is about without opening anything.</summary>
    static Element SummaryRow(int index, string title, string body) => new BoxEl
    {
        Direction = 0, Gap = 10f, Shrink = 0f, AlignItems = FlexAlign.Start, AlignSelf = FlexAlign.Stretch,
        Children =
        [
            NumberChip(index),
            new BoxEl
            {
                Direction = 1, Gap = 1f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    new TextEl(title) { Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(Teaser(body)) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
        ],
    };

    /// <summary>The stage's CLOSED state: the agreement as a summary card — header (glyph + title + "4 sections"),
    /// the four sections as a hairline-separated numbered list with teasers, then a footer with the two links the
    /// page owes the reader. The whole card is one click target (<see cref="Interaction.Card"/> hover/press), so the
    /// footer link and the card itself do the same thing; there is no corner seal hanging off the edge any more.</summary>
    static Element AgreementSummary(Action open, Action openPrivacy)
    {
        var sections = Sections();
        var kids = new List<Element>(2 * sections.Length + 3)
        {
            CardHeader(MetaPill(Strings.Setup.Terms.SectionsCount(sections.Length))),
        };
        for (int i = 0; i < sections.Length; i++)
        {
            if (i > 0) kids.Add(Hairline());
            kids.Add(SummaryRow(i, sections[i].Title, sections[i].Body));
        }
        kids.Add(Hairline());
        kids.Add(new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Center,
            Justify = FlexJustify.SpaceBetween,
            Children =
            [
                HyperlinkButton.Create(Loc.Get(Strings.Setup.Terms.ReadFull), open, size: ControlSize.Small),
                HyperlinkButton.Create(Loc.Get(Strings.Setup.Terms.PrivacyLink), openPrivacy, size: ControlSize.Small),
            ],
        });

        return new BoxEl
        {
            Direction = 1, Gap = 10f, Shrink = 0f, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
            Padding = new Edges4(14f, 12f, 14f, 12f),
            Corners = Radii.CardAll, Shadow = Elevation.Card,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
            Children = kids.ToArray(),
        // Fill + stroke + the press geometry come from the recipe, so the card's resting/hover/pressed faces stay
        // theme-live and match every other card surface in the wizard.
        }.Interactive(Interaction.Card);
    }

    // ── Open state: the same card, grown into the document ──────────────────────────────────────────────────────

    /// <summary>One document section: the same accent number chip as the summary, its title, and the FULL body,
    /// wrapped. 12.5/17 secondary — one notch above the wizard's fine print, because this is the text the user is
    /// actually being asked to agree to.</summary>
    static Element DocSection(int index, string title, string body) => new BoxEl
    {
        Direction = 0, Gap = 10f, Shrink = 0f, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Start,
        Children =
        [
            NumberChip(index),
            new BoxEl
            {
                Direction = 1, Gap = 3f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    new TextEl(title) { Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                    new TextEl(body) { Size = 12.5f, LineHeight = 17f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                ],
            },
        ],
    };

    /// <summary>The full four-section agreement, in its own card — the closed summary grown into the document.
    ///
    /// <para><paramref name="fill"/> is the STAGE variant: the card takes the whole stage column and the sections
    /// scroll INSIDE it. Inline (Compact/Narrow) it is false, and the card sizes to its content instead: the page
    /// body there is already a <c>ScrollEl</c>, and a <c>ScrollView</c> nested in a content-sized viewport is handed
    /// no height to scroll in — it would simply never scroll.</para>
    ///
    /// <para>Escape closes it exactly like the rest of the wizard's nested popups close on Escape without touching
    /// the plate's own modal <c>DismissBehavior</c>; <paramref name="onRealized"/> moves focus into the card on open
    /// so that Escape has somewhere to land.</para></summary>
    static Element AgreementDoc(Action close, bool fill, Action<NodeHandle> onRealized)
    {
        var sections = Sections();
        var kids = new List<Element>(2 * sections.Length);
        for (int i = 0; i < sections.Length; i++)
        {
            if (i > 0) kids.Add(Hairline());
            kids.Add(DocSection(i, sections[i].Title, sections[i].Body));
        }

        BoxEl stack = new()
        {
            Direction = 1, Gap = 14f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = kids.ToArray(),
        };

        Element body = fill
            ? ScrollView(stack) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f }
            : stack with { Shrink = 0f };

        // A full-width Close button, not the ✕ alone: the header glyph is a 24-DIP target in a 296-DIP column, and
        // "how do I get back?" is the one question this state must never make the reader hunt for. Both spellings
        // stay — plus Escape, which the card owns because it is the focused node.
        Element footer = Button.Standard(Loc.Get(Strings.Setup.Terms.Close), close) with
        {
            AlignSelf = FlexAlign.Stretch, Shrink = 0f,
        };

        BoxEl card = new()
        {
            Direction = 1, Gap = 12f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Padding = new Edges4(14f, 12f, 14f, 12f),
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Focusable = true,
            OnRealized = onRealized,
            OnKeyDown = e => { if (e.KeyCode == Keys.Escape) { close(); e.Handled = true; } },
            Children =
            [
                CardHeader(IconButton.Create(Icons.Cancel, close, size: ControlSize.Small)),
                body,
                footer,
            ],
        };

        return fill ? card with { Grow = 1f, Shrink = 1f, MinHeight = 0f } : card with { Shrink = 0f };
    }
}
