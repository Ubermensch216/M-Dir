namespace MDir.Panes;

/// <summary>
/// Back/forward history kept by each tab instead of the Explorer
/// browser's own travel log, so it survives the browser instance being recreated.
/// </summary>
public sealed class NavigationHistory<T> where T : class
{
    private readonly List<T> _back = [];
    private readonly List<T> _forward = [];

    public NavigationHistory(int capacity = 100) => Capacity = capacity;

    public int Capacity { get; }

    public T? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public T? PeekBack() => CanGoBack ? _back[^1] : null;

    public T? PeekForward() => CanGoForward ? _forward[^1] : null;

    /// <summary>Records a new location; clears forward history.</summary>
    public void Visit(T entry)
    {
        if (Current is not null)
        {
            if (Current.Equals(entry))
                return;

            _back.Add(Current);
            if (_back.Count > Capacity)
                _back.RemoveAt(0);
        }

        _forward.Clear();
        Current = entry;
    }

    /// <summary>Independent copy (entries are shared, lists are not), used when a tab is duplicated.</summary>
    public NavigationHistory<T> Clone()
    {
        var copy = new NavigationHistory<T>(Capacity) { Current = Current };
        copy._back.AddRange(_back);
        copy._forward.AddRange(_forward);
        return copy;
    }

    public void MoveBack()
    {
        if (!CanGoBack)
            throw new InvalidOperationException("No back history.");

        _forward.Add(Current!);
        Current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
    }

    public void MoveForward()
    {
        if (!CanGoForward)
            throw new InvalidOperationException("No forward history.");

        _back.Add(Current!);
        Current = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
    }
}
