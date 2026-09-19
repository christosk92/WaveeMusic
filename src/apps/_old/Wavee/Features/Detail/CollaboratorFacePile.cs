using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Playlist collaborator control: owner-first avatar stack plus an interactive flyout of resolved contributors.
sealed class CollaboratorFacePile : Component
{
    readonly DetailModel _seed;
    readonly float _maxWidth;
    readonly Loadable<DetailModel>? _full;

    const float Avatar = 28f, Ring = 2f, Outer = Avatar + Ring * 2f, Overlap = 12f;
    const int MaxVisible = 4;

    public CollaboratorFacePile(DetailModel m, float maxWidth, Loadable<DetailModel>? full = null)
    {
        _seed = m;
        _maxWidth = maxWidth;
        _full = full;
    }

    public override Element Render()
    {
        // Read INSIDE Render — that read is the subscription that re-runs this component when a resolved
        // User entity lands and DetailPage re-maps the model (StoreLibrarySource.BuildCollaborators / UserHydration).
        // A ctor-captured list (the old bug) freezes the raw-id placeholders forever since the pile never remounts.
        var m = _full?.Value.Value ?? _seed;
        var members = m.Collaborators ?? Array.Empty<Owner>();
        bool isCollaborative = m.Capabilities.IsCollaborative;
        if (members.Count == 0) return new BoxEl();
        int overflow = Math.Max(0, members.Count - MaxVisible);
        string label = members.Count >= 2
            ? members.Count + " collaborators"
            : isCollaborative ? "Open to collaboration" : members[0].Name;

        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);

        void Toggle()
        {
            if (overlay is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => Flyout(members, () => handle.Value?.Close()),
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
                FaceStack(members, overflow),
                Icon(Icons.ChevronDownSmall, 8f, Tok.TextTertiary),
            ],
        };

        var rowKids = new List<Element>(3)
        {
            ToolTip.Wrap(button, "View collaborators"),
            new TextEl(label) { Size = 14f, Weight = 700, Color = Tok.AccentTextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        // Shared adaptive invite affordance — self-gates (empty when the viewer can't administer permissions) and opens
        // the Invite & access flyout instead of insta-copying, replacing the old duplicated gray pill.
        if (_full is not null) rowKids.Add(PlaylistInlineEdit.InviteButton(_full, _maxWidth));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MaxWidth = _maxWidth,
            Children = rowKids.ToArray(),
        };
    }

    static Element FaceStack(IReadOnlyList<Owner> members, int overflow)
    {
        int visible = Math.Min(MaxVisible, members.Count);
        var kids = new List<Element>(visible + (overflow > 0 ? 1 : 0));
        for (int i = 0; i < visible; i++) kids.Add(AvatarFrame(members[i], i == 0));
        if (overflow > 0) kids.Add(OverflowFrame(overflow, visible == 0));
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
    }

    static Element AvatarFrame(Owner owner, bool first) => new BoxEl
    {
        Width = Outer, Height = Outer, Shrink = 0f, Corners = CornerRadius4.All(Outer / 2f),
        Fill = Tok.FillSolidBase, Padding = Edges4.All(Ring),
        Margin = new Edges4(first ? 0f : -Overlap, 0f, 0f, 0f),
        Children = [PersonPicture.Create("", Avatar, displayName: owner.Name, imageSourcePath: owner.Avatar?.Url)],
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

    static Element Flyout(IReadOnlyList<Owner> members, Action close)
    {
        var rows = new Element[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            var owner = members[i];
            rows[i] = new BoxEl
            {
                Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                Corners = CornerRadius4.All(6f),
                Role = AutomationRole.MenuItem, Focusable = true, Cursor = CursorId.Hand, OnClick = close,
                Children =
                [
                    PersonPicture.Create("", 32f, displayName: owner.Name, imageSourcePath: owner.Avatar?.Url),
                    new TextEl(owner.Name) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
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
}
