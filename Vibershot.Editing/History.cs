namespace Vibershot.Editing;

/// <summary>
/// Undo/redo over immutable snapshots. Pushes that share a coalesce key (one drag, one
/// nudge run) replace the top entry instead of adding a step, so a whole gesture undoes
/// at once. The depth limit drops the oldest undo entries.
/// </summary>
public sealed class History<T> where T : class
{
    private readonly List<T> _undo = new();
    private readonly Stack<T> _redo = new();
    private readonly int _limit;
    private string? _lastKey;
    private T _saved;

    public History(T initial, int limit = 200)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        Current = initial;
        _saved = initial;
        _limit = limit;
    }

    public T Current { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoDepth => _undo.Count;

    /// <summary>True when the current state differs from the last <see cref="MarkSaved"/> point.</summary>
    public bool IsDirty => !ReferenceEquals(Current, _saved);

    /// <summary>Raised after every change to <see cref="Current"/>.</summary>
    public event Action? Changed;

    /// <summary>
    /// Records a new state. When <paramref name="coalesceKey"/> matches the key of the previous
    /// push, the previous state is replaced rather than kept as a separate undo step.
    /// </summary>
    public void Push(T state, string? coalesceKey = null)
    {
        if (ReferenceEquals(state, Current)) return;
        bool coalesce = coalesceKey != null && coalesceKey == _lastKey;
        if (!coalesce)
        {
            _undo.Add(Current);
            if (_undo.Count > _limit) _undo.RemoveAt(0);
        }
        Current = state;
        _redo.Clear();
        _lastKey = coalesceKey;
        Changed?.Invoke();
    }

    /// <summary>Ends the current coalescing run so the next push with the same key starts a new step.</summary>
    public void BreakCoalescing() => _lastKey = null;

    /// <summary>
    /// Discards the coalesced step identified by <paramref name="coalesceKey"/> (for example a
    /// drag that ended up too small to keep) without leaving a redo entry.
    /// </summary>
    public void Rollback(string coalesceKey)
    {
        if (_lastKey != coalesceKey || _undo.Count == 0) return;
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _lastKey = null;
        Changed?.Invoke();
    }

    public T Undo()
    {
        if (_undo.Count == 0) return Current;
        _redo.Push(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _lastKey = null;
        Changed?.Invoke();
        return Current;
    }

    public T Redo()
    {
        if (_redo.Count == 0) return Current;
        _undo.Add(Current);
        Current = _redo.Pop();
        _lastKey = null;
        Changed?.Invoke();
        return Current;
    }

    /// <summary>Marks the current state as matching what is on disk.</summary>
    public void MarkSaved() => _saved = Current;

    /// <summary>Replaces everything with a fresh starting state (after loading a project).</summary>
    public void Reset(T state)
    {
        _undo.Clear();
        _redo.Clear();
        _lastKey = null;
        Current = state;
        _saved = state;
        Changed?.Invoke();
    }
}
