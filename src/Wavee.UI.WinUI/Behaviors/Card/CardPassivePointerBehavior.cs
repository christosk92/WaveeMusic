using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Controls.Cards;

namespace Wavee.UI.WinUI.Behaviors.Card;

/// <summary>
/// Attached behavior on <see cref="ContentCard"/>. When the host is in passive
/// mode (the card lives inside an <c>ItemsView</c>/<c>ItemContainer</c> whose
/// selection chrome marks pointer events as <c>Handled</c>), the standard XAML
/// <c>PointerEntered</c> etc. attribute wiring stops firing — selection chrome
/// consumes the event before bubbling. This behavior re-registers the same
/// handlers via <c>UIElement.AddHandler(handledEventsToo:true)</c> so the hover
/// scale, image opacity bump, play-overlay reveal, and press animation keep
/// working alongside the parent's selection chrome.
///
/// <para>The handlers themselves still live on <see cref="ContentCard"/> — this
/// behavior is a thin "register with handledEventsToo" wrapper. State (the four
/// handler delegate instances + an attached flag) is held in a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed on the card instance so
/// the GC can reclaim recycled cards.</para>
///
/// <para>Wire-up: <c>card:CardPassivePointerBehavior.IsAttached="True"</c> in
/// XAML on the <see cref="ContentCard"/> root. The behavior is keyed off this
/// flag rather than the card's <c>IsPassive</c> DP so XAML stays explicit and
/// the same control can opt in or out independently of <c>IsPassive</c> (e.g.
/// during template-only previews).</para>
/// </summary>
public static class CardPassivePointerBehavior
{
    private static readonly ConditionalWeakTable<ContentCard, HandlerHolder> _holders = new();

    private sealed class HandlerHolder
    {
        public bool Attached;
        public PointerEventHandler? Entered;
        public PointerEventHandler? Exited;
        public PointerEventHandler? Pressed;
        public PointerEventHandler? Released;
    }

    public static readonly DependencyProperty IsAttachedProperty =
        DependencyProperty.RegisterAttached(
            "IsAttached",
            typeof(bool),
            typeof(CardPassivePointerBehavior),
            new PropertyMetadata(false, OnIsAttachedChanged));

    public static bool GetIsAttached(DependencyObject obj) => (bool)obj.GetValue(IsAttachedProperty);
    public static void SetIsAttached(DependencyObject obj, bool value) => obj.SetValue(IsAttachedProperty, value);

    private static void OnIsAttachedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentCard card) return;

        // The card's own IsPassive DP drives the actual semantic — we only wire
        // handlers when the card runs in a selection-chrome host. Attaching the
        // behavior unconditionally would silently re-fire hover handlers twice on
        // non-passive cards (once from XAML, once from the bubble path).
        card.Loaded -= OnCardLoaded;
        card.Unloaded -= OnCardUnloaded;
        Detach(card);

        if (e.NewValue is not true)
            return;

        card.Loaded += OnCardLoaded;
        card.Unloaded += OnCardUnloaded;

        // Card may already be loaded if behavior is wired post-Loaded (rare, but
        // happens when a DP flips after template apply).
        if (card.IsLoaded)
            TryAttachIfPassive(card);
    }

    private static void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentCard card)
            TryAttachIfPassive(card);
    }

    private static void OnCardUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentCard card)
            Detach(card);
    }

    private static void TryAttachIfPassive(ContentCard card)
    {
        if (!card.IsPassive) return;
        if (_holders.TryGetValue(card, out var existing) && existing.Attached) return;

        var holder = existing ?? new HandlerHolder();

        // Reuse the card's existing hover/press methods. They were public-static
        // before; they're now internal-instance on ContentCard so the behavior
        // can dispatch into them without the card needing to know about us.
        holder.Entered ??= new PointerEventHandler(card.HandlePassivePointerEntered);
        holder.Exited ??= new PointerEventHandler(card.HandlePassivePointerExited);
        holder.Pressed ??= new PointerEventHandler(card.HandlePassivePointerPressed);
        holder.Released ??= new PointerEventHandler(card.HandlePassivePointerReleased);

        // handledEventsToo=true: keep firing even when the parent ItemContainer
        // marks the pointer event as handled (selection chrome).
        card.AddHandler(UIElement.PointerEnteredEvent, holder.Entered, true);
        card.AddHandler(UIElement.PointerExitedEvent, holder.Exited, true);
        card.AddHandler(UIElement.PointerPressedEvent, holder.Pressed, true);
        card.AddHandler(UIElement.PointerReleasedEvent, holder.Released, true);

        holder.Attached = true;
        _holders.AddOrUpdate(card, holder);
    }

    private static void Detach(ContentCard card)
    {
        if (!_holders.TryGetValue(card, out var holder) || !holder.Attached)
            return;

        if (holder.Entered is not null)
            card.RemoveHandler(UIElement.PointerEnteredEvent, holder.Entered);
        if (holder.Exited is not null)
            card.RemoveHandler(UIElement.PointerExitedEvent, holder.Exited);
        if (holder.Pressed is not null)
            card.RemoveHandler(UIElement.PointerPressedEvent, holder.Pressed);
        if (holder.Released is not null)
            card.RemoveHandler(UIElement.PointerReleasedEvent, holder.Released);

        holder.Attached = false;
        // Keep the delegate instances cached on the holder so re-attach (after
        // recycle) doesn't re-allocate; recycle is the hot path.
    }
}
