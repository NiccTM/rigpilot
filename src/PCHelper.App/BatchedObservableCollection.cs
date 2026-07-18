using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.ObjectModel;

namespace PCHelper.App;

/// <summary>
/// Reconciles an observable collection without publishing every intermediate
/// insert, move, replacement, or removal to WPF. Equal items keep their object
/// identity; a changed batch produces one Reset notification when the outermost
/// deferral ends.
/// </summary>
internal sealed class BatchedObservableCollection<T> : ObservableCollection<T>, INotificationBatchCollection
{
    private int _deferLevel;
    private bool _collectionChanged;
    private bool _countChanged;
    private bool _itemsChanged;

    public IDisposable DeferNotifications()
    {
        _deferLevel++;
        return new DeferredAction(EndDefer);
    }

    public void Synchronize(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        T[] desired = source.ToArray();
        if (this.SequenceEqual(desired))
        {
            return;
        }

        using IDisposable batch = DeferNotifications();
        int sharedCount = Math.Min(Count, desired.Length);
        for (int index = 0; index < sharedCount; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(this[index], desired[index]))
            {
                SetItem(index, desired[index]);
            }
        }

        while (Count > desired.Length)
        {
            RemoveItem(Count - 1);
        }

        for (int index = Count; index < desired.Length; index++)
        {
            InsertItem(index, desired[index]);
        }
    }

    public void SynchronizeByKey<TKey>(
        IEnumerable<T> source,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);
        keyComparer ??= EqualityComparer<TKey>.Default;
        (T Item, TKey Key)[] desired = source
            .Select(item => (item, keySelector(item)))
            .ToArray();

        bool unchanged = Count == desired.Length;
        for (int index = 0; unchanged && index < desired.Length; index++)
        {
            unchanged = keyComparer.Equals(keySelector(this[index]), desired[index].Key)
                && EqualityComparer<T>.Default.Equals(this[index], desired[index].Item);
        }
        if (unchanged)
        {
            return;
        }

        using IDisposable batch = DeferNotifications();
        for (int desiredIndex = 0; desiredIndex < desired.Length; desiredIndex++)
        {
            (T desiredItem, TKey desiredKey) = desired[desiredIndex];
            int currentIndex = FindIndex(desiredIndex, desiredKey, keySelector, keyComparer);
            if (currentIndex < 0)
            {
                InsertItem(desiredIndex, desiredItem);
                continue;
            }

            if (currentIndex != desiredIndex)
            {
                MoveItem(currentIndex, desiredIndex);
            }

            if (!EqualityComparer<T>.Default.Equals(this[desiredIndex], desiredItem))
            {
                SetItem(desiredIndex, desiredItem);
            }
        }

        while (Count > desired.Length)
        {
            RemoveItem(Count - 1);
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_deferLevel > 0)
        {
            _collectionChanged = true;
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_deferLevel > 0)
        {
            _countChanged |= string.Equals(e.PropertyName, nameof(Count), StringComparison.Ordinal);
            _itemsChanged |= string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal);
            return;
        }

        base.OnPropertyChanged(e);
    }

    private int FindIndex<TKey>(
        int startIndex,
        TKey desiredKey,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey> keyComparer)
    {
        for (int index = startIndex; index < Count; index++)
        {
            if (keyComparer.Equals(keySelector(this[index]), desiredKey))
            {
                return index;
            }
        }

        return -1;
    }

    private void EndDefer()
    {
        if (--_deferLevel > 0 || !_collectionChanged)
        {
            return;
        }

        bool countChanged = _countChanged;
        bool itemsChanged = _itemsChanged;
        _collectionChanged = false;
        _countChanged = false;
        _itemsChanged = false;
        if (countChanged)
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        }
        if (itemsChanged || countChanged)
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        }
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

internal interface INotificationBatchCollection
{
    IDisposable DeferNotifications();
}

internal static class CollectionNotificationBatch
{
    public static IDisposable Defer(params object[] collections)
    {
        List<IDisposable> deferrals = [];
        try
        {
            foreach (INotificationBatchCollection collection in collections.OfType<INotificationBatchCollection>())
            {
                deferrals.Add(collection.DeferNotifications());
            }
        }
        catch
        {
            DisposeReverse(deferrals);
            throw;
        }

        return new DeferredAction(() => DisposeReverse(deferrals));
    }

    private static void DisposeReverse(List<IDisposable> deferrals)
    {
        for (int index = deferrals.Count - 1; index >= 0; index--)
        {
            deferrals[index].Dispose();
        }
    }
}

internal sealed class DeferredAction(Action action) : IDisposable
{
    private Action? _action = action ?? throw new ArgumentNullException(nameof(action));

    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}
