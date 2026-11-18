using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Tandem.App.ViewModels;

/// <summary>
/// A read-only list of rows that is replaced as a whole: one <see cref="NotifyCollectionChangedAction.Reset"/> per
/// replacement, never per-item notifications, so a 100,000-row listing costs one notification. Implements the
/// non-generic <see cref="IList"/> so item controls index it instead of copying it. UI thread only.
/// </summary>
internal sealed class RowCollection : IReadOnlyList<EntryRowViewModel>, IList, INotifyCollectionChanged,
    INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

    private IReadOnlyList<EntryRowViewModel> _items = [];

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The current rows; the list is kept as is, so the caller must not change it afterwards.</summary>
    public IReadOnlyList<EntryRowViewModel> Items => _items;

    public void Replace(IReadOnlyList<EntryRowViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var countChanged = items.Count != _items.Count;
        _items = items;
        if (countChanged) PropertyChanged?.Invoke(this, CountChanged);
        PropertyChanged?.Invoke(this, IndexerChanged);
        CollectionChanged?.Invoke(this, ResetArgs);
    }

    public int Count => _items.Count;

    public EntryRowViewModel this[int index] => _items[index];

    public IEnumerator<EntryRowViewModel> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- IList (read-only) ----------------------------------------------------------------------------

    object? IList.this[int index]
    {
        get => _items[index];
        set => throw ReadOnly();
    }

    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    int IList.IndexOf(object? value)
    {
        if (value is not EntryRowViewModel row) return -1;
        for (var i = 0; i < _items.Count; i++)
            if (ReferenceEquals(_items[i], row)) return i;
        return -1;
    }

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var i = 0; i < _items.Count; i++) array.SetValue(_items[i], index + i);
    }

    int IList.Add(object? value) => throw ReadOnly();
    void IList.Clear() => throw ReadOnly();
    void IList.Insert(int index, object? value) => throw ReadOnly();
    void IList.Remove(object? value) => throw ReadOnly();
    void IList.RemoveAt(int index) => throw ReadOnly();

    private static NotSupportedException ReadOnly() => new("The rows are read-only.");
}
