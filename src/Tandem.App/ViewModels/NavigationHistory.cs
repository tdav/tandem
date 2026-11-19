using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>
/// The folders one tab has shown, for "back" and "forward", like a browser: a new visit drops the entries ahead of
/// the current one, the same folder twice in a row is kept once, and only the newest <see cref="Capacity"/> entries
/// are kept. <see cref="Back"/> and <see cref="Forward"/> only move the position; they never add entries.
/// </summary>
public sealed class NavigationHistory
{
    public const int Capacity = 50;

    private readonly List<VfsPath> _entries = [];
    private int _position = -1;

    public bool CanGoBack => _position > 0;

    public bool CanGoForward => _position >= 0 && _position < _entries.Count - 1;

    /// <summary>Each folder of the history once, the latest entry first.</summary>
    public IReadOnlyList<VfsPath> Recent
    {
        get
        {
            var recent = new List<VfsPath>(_entries.Count);
            for (var i = _entries.Count - 1; i >= 0; i--)
                if (!recent.Contains(_entries[i]))
                    recent.Add(_entries[i]);
            return recent;
        }
    }

    /// <summary>Records <paramref name="path"/> as the folder now shown.</summary>
    public void Visit(VfsPath path)
    {
        if (_position >= 0 && _entries[_position] == path) return;
        _entries.RemoveRange(_position + 1, _entries.Count - _position - 1);
        _entries.Add(path);
        if (_entries.Count > Capacity) _entries.RemoveAt(0);
        _position = _entries.Count - 1;
    }

    /// <summary>Moves one entry back; the folder there, or null when there is none.</summary>
    public VfsPath? Back()
    {
        if (!CanGoBack) return null;
        return _entries[--_position];
    }

    /// <summary>Moves one entry forward; the folder there, or null when there is none.</summary>
    public VfsPath? Forward()
    {
        if (!CanGoForward) return null;
        return _entries[++_position];
    }

    /// <summary>The folder one step back (<paramref name="step"/> = -1) or forward (+1), without moving.</summary>
    internal VfsPath? Peek(int step)
    {
        var index = _position + step;
        return _position >= 0 && index >= 0 && index < _entries.Count ? _entries[index] : null;
    }

    /// <summary>Drops the entry one step back or forward, a folder that could not be shown; the position stays on
    /// the current entry.</summary>
    internal void RemoveAt(int step)
    {
        var index = _position + step;
        if (_position < 0 || index < 0 || index >= _entries.Count) return;
        _entries.RemoveAt(index);
        if (index < _position) _position--;

        // The neighbours of the dropped entry may now be the same folder twice in a row.
        var left = Math.Min(index, _entries.Count - 1) - 1;
        if (left >= 0 && _entries[left] == _entries[left + 1])
        {
            _entries.RemoveAt(left + 1);
            if (left + 1 <= _position) _position--;
        }
    }
}
