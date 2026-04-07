using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Wavee.UI.WinUI.Collections;

public class FullyObservableCollection<T> : ObservableCollection<T>
    where T : INotifyPropertyChanged
{
    public event EventHandler<ItemPropertyChangedEventArgs>? ItemPropertyChanged;

    public FullyObservableCollection() { }

    public FullyObservableCollection(List<T> list) : base(list)
    {
        ObserveAll();
    }

    public FullyObservableCollection(IEnumerable<T> enumerable) : base(enumerable)
    {
        ObserveAll();
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace)
        {
            foreach (T item in e.OldItems!)
                item.PropertyChanged -= ChildPropertyChanged;
        }

        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace)
        {
            foreach (T item in e.NewItems!)
                item.PropertyChanged += ChildPropertyChanged;
        }

        base.OnCollectionChanged(e);
    }

    protected override void ClearItems()
    {
        foreach (var item in Items)
            item.PropertyChanged -= ChildPropertyChanged;

        base.ClearItems();
    }

    private void ObserveAll()
    {
        foreach (var item in Items)
            item.PropertyChanged += ChildPropertyChanged;
    }

    private void ChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is T typedSender)
        {
            var i = Items.IndexOf(typedSender);
            if (i >= 0)
                ItemPropertyChanged?.Invoke(this, new ItemPropertyChangedEventArgs(i, e));
        }
    }
}

public class ItemPropertyChangedEventArgs : PropertyChangedEventArgs
{
    public int CollectionIndex { get; }

    public ItemPropertyChangedEventArgs(int index, string? name) : base(name)
    {
        CollectionIndex = index;
    }

    public ItemPropertyChangedEventArgs(int index, PropertyChangedEventArgs args) : this(index, args.PropertyName)
    { }
}
