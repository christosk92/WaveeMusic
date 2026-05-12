using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Click-to-edit-in-place text. Idle: renders <see cref="Text"/> as a TextBlock.
/// Hover (when <see cref="IsEditable"/>): subtle frame appears. Click: swap to a
/// TextBox in the same slot. Enter (or focus loss) commits via <see cref="Committed"/>.
/// Esc cancels via <see cref="Cancelled"/>.
///
/// The control is visually neutral when <see cref="IsEditable"/> is false — it
/// renders as a plain TextBlock with no hover affordance, so it can drop in
/// wherever a TextBlock already lives without changing the read-only experience.
/// </summary>
public sealed partial class InlineEditableText : UserControl
{
    public InlineEditableText()
    {
        InitializeComponent();
        UpdatePlaceholderVisibility();
    }

    // ── Dependency properties ───────────────────────────────────────────────

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(InlineEditableText),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty IsEditableProperty =
        DependencyProperty.Register(nameof(IsEditable), typeof(bool), typeof(InlineEditableText),
            new PropertyMetadata(false, OnIsEditableChanged));

    /// <summary>Gates hover affordance and click-to-edit. False ⇒ behaves like a TextBlock.</summary>
    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public static readonly DependencyProperty AcceptsReturnProperty =
        DependencyProperty.Register(nameof(AcceptsReturn), typeof(bool), typeof(InlineEditableText),
            new PropertyMetadata(false));

    /// <summary>If true, plain Enter inserts a newline and Ctrl+Enter commits. If false, Enter commits.</summary>
    public bool AcceptsReturn
    {
        get => (bool)GetValue(AcceptsReturnProperty);
        set => SetValue(AcceptsReturnProperty, value);
    }

    public static readonly DependencyProperty MaxLengthProperty =
        DependencyProperty.Register(nameof(MaxLength), typeof(int), typeof(InlineEditableText),
            new PropertyMetadata(0));

    public int MaxLength
    {
        get => (int)GetValue(MaxLengthProperty);
        set => SetValue(MaxLengthProperty, value);
    }

    public static readonly DependencyProperty MaxLinesProperty =
        DependencyProperty.Register(nameof(MaxLines), typeof(int), typeof(InlineEditableText),
            new PropertyMetadata(0));

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(InlineEditableText),
            new PropertyMetadata(string.Empty, OnPlaceholderChanged));

    /// <summary>Muted text shown when <see cref="Text"/> is empty AND <see cref="IsEditable"/>. e.g. "Add a description".</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly DependencyProperty TextAlignmentProperty =
        DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(InlineEditableText),
            new PropertyMetadata(TextAlignment.Left));

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(InlineEditableText),
            new PropertyMetadata(false, OnIsBusyChanged));

    /// <summary>Drives the inline ProgressRing. Consumer toggles while the save is in flight.</summary>
    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    // ── Events ──────────────────────────────────────────────────────────────

    /// <summary>Fired when the user commits an edit (Enter / Ctrl+Enter / focus-loss).</summary>
    public event EventHandler<string>? Committed;

    /// <summary>Fired when the user cancels an edit (Esc).</summary>
    public event EventHandler? Cancelled;

    // ── State ───────────────────────────────────────────────────────────────

    private bool _isEditing;
    private bool _isHovering;
    private bool _suppressLostFocusCommit;

    // The TextBox driven by the current edit session. EnterEditMode picks
    // EditBoxSingle or EditBoxMulti based on AcceptsReturn; the rest of the
    // file routes Text / Focus / SelectAll / handler logic through this field
    // so neither layout host needs duplicated code.
    private TextBox? _activeEditBox;

    // ── Handlers ────────────────────────────────────────────────────────────

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((InlineEditableText)d).UpdatePlaceholderVisibility();

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((InlineEditableText)d).UpdatePlaceholderVisibility();

    private static void OnIsEditableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((InlineEditableText)d).UpdatePlaceholderVisibility();

    private static void OnIsBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (InlineEditableText)d;
        var busy = (bool)e.NewValue;
        c.BusyRing.IsActive = busy;
        c.BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // Hide the pencil while a save is in flight; the spinner takes its slot.
        c.UpdatePlaceholderVisibility();
    }

    private void UpdatePlaceholderVisibility()
    {
        if (PlaceholderText == null || DisplayText == null) return;

        var showPlaceholder = IsEditable
            && !_isEditing
            && string.IsNullOrEmpty(Text)
            && !string.IsNullOrEmpty(Placeholder);

        PlaceholderText.Visibility = showPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        DisplayText.Visibility = (_isEditing || showPlaceholder)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Hover-only pencil hint — only shown while the pointer is over the
        // control. Persistent visibility was confirmed too loud; the existing
        // HoverFrame tint already announces "click to edit", and the pencil
        // adds an explicit affordance without permanently cluttering the row.
        if (EditAffordanceButton != null)
        {
            EditAffordanceButton.Visibility = IsEditable && _isHovering && !_isEditing && !IsBusy
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void EditAffordanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditable || _isEditing) return;
        EnterEditMode();
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = true;
        UpdatePlaceholderVisibility();
        if (!IsEditable || _isEditing) return;
        HoverFrame.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        HoverFrame.BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = false;
        UpdatePlaceholderVisibility();
        if (_isEditing) return;
        HoverFrame.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HoverFrame.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = null;
    }

    private void Frame_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsEditable || _isEditing) return;

        // Ignore taps that bubbled out of the edit host (Save / Cancel buttons
        // live inside it). Sequence on Save: SaveButton.Click → CommitEdit →
        // ExitEditMode (which flips _isEditing=false and collapses EditStack)
        // → the same tap then bubbles to this Border. Without this guard the
        // handler re-enters edit mode immediately.
        if (e.OriginalSource is DependencyObject src && IsInsideEditStack(src))
            return;

        EnterEditMode();
        e.Handled = true;
    }

    private bool IsInsideEditStack(DependencyObject node)
    {
        if (EditStack is null) return false;
        var current = node;
        while (current != null)
        {
            if (ReferenceEquals(current, EditStack)) return true;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void EnterEditMode()
    {
        _isEditing = true;
        _suppressLostFocusCommit = false;

        // Pick the layout host that matches the current AcceptsReturn setting
        // and route subsequent edit operations through its TextBox.
        if (AcceptsReturn)
        {
            _activeEditBox = EditBoxMulti;
            SingleLineHost.Visibility = Visibility.Collapsed;
            MultiLineHost.Visibility = Visibility.Visible;
        }
        else
        {
            _activeEditBox = EditBoxSingle;
            MultiLineHost.Visibility = Visibility.Collapsed;
            SingleLineHost.Visibility = Visibility.Visible;
        }

        _activeEditBox.Text = Text ?? string.Empty;
        EditStack.Visibility = Visibility.Visible;
        DisplayText.Visibility = Visibility.Collapsed;
        PlaceholderText.Visibility = Visibility.Collapsed;

        // Frame stays "hover-styled" while editing so the boundary is visible.
        HoverFrame.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        HoverFrame.BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];

        _activeEditBox.Focus(FocusState.Programmatic);
        _activeEditBox.SelectAll();
    }

    private void ExitEditMode(bool restoreHover)
    {
        _isEditing = false;
        // Hide the entire host (was incorrectly hiding only EditBox before, leaving
        // the buttons row visible after Save/Cancel).
        EditStack.Visibility = Visibility.Collapsed;
        _activeEditBox = null;
        ProtectedCursor = null;

        UpdatePlaceholderVisibility();

        if (!restoreHover)
        {
            HoverFrame.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            HoverFrame.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void EditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            // Esc cancels: don't treat the imminent focus-loss as a commit.
            _suppressLostFocusCommit = true;
            ExitEditMode(restoreHover: false);
            Cancelled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            // Multiline: Enter inserts a newline; Ctrl+Enter commits.
            // Single-line: Enter commits.
            if (AcceptsReturn && !IsCtrlPressed())
                return; // let TextBox handle the newline

            CommitEdit();
            e.Handled = true;
        }
    }

    private void EditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isEditing || _suppressLostFocusCommit) return;
        CommitEdit();
    }

    // ── Button handlers ─────────────────────────────────────────────────────

    // Wired from both SaveButtonSingle and SaveButtonMulti — same behaviour.
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditing) return;
        CommitEdit();
    }

    // PointerPressed fires synchronously on press, before focus actually
    // moves to the button — so setting the suppress flag here keeps the
    // imminent TextBox.LostFocus from auto-committing the in-flight edit.
    // Without this the click-to-Cancel would commit then revert, racing
    // the consumer's commit handler.
    private void CancelButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isEditing) _suppressLostFocusCommit = true;
    }

    // Wired from both CancelButtonSingle and CancelButtonMulti.
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditing) return;
        _suppressLostFocusCommit = true;
        ExitEditMode(restoreHover: false);
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void CommitEdit()
    {
        var newValue = _activeEditBox?.Text ?? string.Empty;
        var oldValue = Text ?? string.Empty;
        ExitEditMode(restoreHover: false);

        if (!string.Equals(newValue, oldValue, StringComparison.Ordinal))
            Committed?.Invoke(this, newValue);
    }

    private static bool IsCtrlPressed()
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (ctrl & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }
}
