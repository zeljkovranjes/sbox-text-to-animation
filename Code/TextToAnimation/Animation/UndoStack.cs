#nullable enable annotations

namespace TextToAnimation.Animation;

/// <summary>
/// Snapshot-based undo/redo for one clip. Before every edit the editor calls <see cref="Record"/> with a
/// short description; undo swaps the clip's state with the snapshot. Snapshots are deep copies, bounded by
/// <see cref="MaxSteps"/> and an approximate memory budget so long clips can't exhaust memory.
/// </summary>
public sealed class UndoStack
{
    readonly LinkedList<(string Label, AnimClip State)> _undo = new();
    readonly Stack<(string Label, AnimClip State)> _redo = new();

    public int MaxSteps { get; set; } = 100;

    /// <summary>Approximate cap on snapshot memory in bytes (default 256 MB).</summary>
    public long MemoryBudget { get; set; } = 256L * 1024 * 1024;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Last?.Value.Label;
    public string? RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;

    /// <summary>Raised after any change to the stacks (for enabling buttons).</summary>
    public event Action? Changed;

    /// <summary>Stores the clip's current state before an edit described by <paramref name="label"/>.</summary>
    public void Record(AnimClip clip, string label)
    {
        _undo.AddLast((label, clip.CloneDeep()));
        _redo.Clear();
        Trim();
        Changed?.Invoke();
    }

    /// <summary>Restores the previous state into <paramref name="clip"/>. Returns the undone label or null.</summary>
    public string? Undo(AnimClip clip)
    {
        if (_undo.Last is not { } node) return null;
        _undo.RemoveLast();
        _redo.Push((node.Value.Label, clip.CloneDeep()));
        CopyInto(node.Value.State, clip);
        Changed?.Invoke();
        return node.Value.Label;
    }

    /// <summary>Re-applies the last undone edit. Returns its label or null.</summary>
    public string? Redo(AnimClip clip)
    {
        if (_redo.Count == 0) return null;
        var (label, state) = _redo.Pop();
        _undo.AddLast((label, clip.CloneDeep()));
        CopyInto(state, clip);
        Changed?.Invoke();
        return label;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    void Trim()
    {
        while (_undo.Count > MaxSteps) _undo.RemoveFirst();
        long bytes = 0;
        for (var n = _undo.Last; n is not null; n = n.Previous)
        {
            bytes += Estimate(n.Value.State);
            if (bytes > MemoryBudget && n.Previous is not null)
            {
                while (_undo.First != n) _undo.RemoveFirst();
                break;
            }
        }
    }

    static long Estimate(AnimClip c) => (long)c.FrameCount * (c.Frames.Count > 0 ? c.Frames[0].Length : 0) * 32 + 1024;

    /// <summary>Copies all state (keeping the target instance, which the UI holds on to).</summary>
    static void CopyInto(AnimClip from, AnimClip to)
    {
        var copy = from.CloneDeep();
        to.Name = copy.Name;
        to.Fps = copy.Fps;
        to.Looping = copy.Looping;
        to.Frames = copy.Frames;
        to.Events = copy.Events;
        to.PinnedFrames = copy.PinnedFrames;
        to.LockedBones = copy.LockedBones;
        to.Keys = copy.Keys;
        to.Origin = copy.Origin;
        to.SourceSequence = copy.SourceSequence;
        to.Generation = copy.Generation;
        to.Export = copy.Export;
        to.Revision++;
    }
}
