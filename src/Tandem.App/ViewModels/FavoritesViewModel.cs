using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Tandem.App.ViewModels;

/// <summary>The user's favourite folders, in the order they were added; each path once.</summary>
/// <remarks>Called on the UI thread.</remarks>
public sealed partial class FavoritesViewModel : ObservableObject
{
    private readonly ObservableCollection<string> _items = [];

    public FavoritesViewModel()
    {
        Items = new ReadOnlyObservableCollection<string>(_items);
    }

    public ReadOnlyObservableCollection<string> Items { get; }

    /// <summary>Raised after <see cref="Add"/> or <see cref="Remove"/> has changed the list; not by <see cref="Load"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Replaces the list with saved items, skipping empty and repeated ones.</summary>
    public void Load(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        foreach (var item in items)
            if (!string.IsNullOrEmpty(item) && !_items.Contains(item))
                _items.Add(item);
    }

    /// <summary>Adds <paramref name="path"/> at the end; does nothing if it is already there.</summary>
    public void Add(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (_items.Contains(path)) return;
        _items.Add(path);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string path)
    {
        if (_items.Remove(path)) Changed?.Invoke(this, EventArgs.Empty);
    }
}
