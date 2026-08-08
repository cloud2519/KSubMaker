using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace KSubMaker.App.Collections;

/// <summary>
/// <see cref="ObservableCollection{T}"/> plus a bulk add.
///
/// A folder scan can enqueue several thousand files at once. Adding them one by one raises one
/// <see cref="INotifyCollectionChanged"/> event per item, and a <c>DataGrid</c> answers every single
/// one with a measure/arrange pass — the UI thread disappears for tens of seconds. Raising a single
/// <see cref="NotifyCollectionChangedAction.Reset"/> instead turns that into one layout pass.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> items)
        : base(items)
    {
    }

    /// <summary>Appends every item and raises exactly one Reset notification.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var added = false;
        _suppressNotifications = true;
        try
        {
            foreach (var item in items)
            {
                Items.Add(item);
                added = true;
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        if (added)
        {
            RaiseReset();
        }
    }

    /// <summary>Replaces the whole contents and raises exactly one Reset notification.</summary>
    public void Reset(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        RaiseReset();
    }

    /// <summary>Removes every item matching <paramref name="predicate"/> with one notification.</summary>
    public int RemoveWhere(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var removed = 0;
        _suppressNotifications = true;
        try
        {
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (predicate(Items[i]))
                {
                    Items.RemoveAt(i);
                    removed++;
                }
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        if (removed > 0)
        {
            RaiseReset();
        }

        return removed;
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressNotifications)
        {
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressNotifications)
        {
            return;
        }

        base.OnPropertyChanged(e);
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
