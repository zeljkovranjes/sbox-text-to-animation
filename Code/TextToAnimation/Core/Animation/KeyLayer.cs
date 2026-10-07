#nullable enable annotations

using System.Numerics;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>
/// Non-destructive pose edits on top of a clip's dense frames. A key stores, for one bone at one frame,
/// the parent-local offset from the underlying motion (translation added, rotation pre-multiplied).
/// Between two keys of a bone the offset is eased; before the first and after the last key it fades out
/// over <see cref="FalloffFrames"/>, so posing a bone at one frame never pops the rest of the clip.
/// </summary>
public sealed class KeyLayer
{
    readonly Dictionary<string, SortedDictionary<int, XForm>> _keys = new(StringComparer.Ordinal);

    /// <summary>Frames over which a key's influence fades in/out beyond the first/last key of a bone.</summary>
    public int FalloffFrames { get; set; } = 8;

    public bool IsEmpty => _keys.Count == 0;

    /// <summary>Bones that have at least one key.</summary>
    public IEnumerable<string> KeyedBones => _keys.Keys;

    /// <summary>Frames that hold a key on any bone (for the timeline).</summary>
    public IEnumerable<int> AllKeyFrames => _keys.Values.SelectMany(k => k.Keys).Distinct().OrderBy(f => f);

    public IReadOnlyCollection<int> KeyFrames(string bone)
        => _keys.TryGetValue(bone, out var k) ? k.Keys : (IReadOnlyCollection<int>)Array.Empty<int>();

    public bool HasKey(string bone, int frame) => _keys.TryGetValue(bone, out var k) && k.ContainsKey(frame);

    public bool TryGetKey(string bone, int frame, out XForm delta)
    {
        delta = XForm.Identity;
        return _keys.TryGetValue(bone, out var k) && k.TryGetValue(frame, out delta);
    }

    public void SetKey(string bone, int frame, XForm delta)
    {
        if (frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
        if (!_keys.TryGetValue(bone, out var k)) _keys[bone] = k = new SortedDictionary<int, XForm>();
        k[frame] = new XForm(delta.Pos, MathQ.Normalize(delta.Rot));
    }

    public bool RemoveKey(string bone, int frame)
    {
        if (!_keys.TryGetValue(bone, out var k) || !k.Remove(frame)) return false;
        if (k.Count == 0) _keys.Remove(bone);
        return true;
    }

    /// <summary>Removes every key at <paramref name="frame"/> (all bones). Returns how many were removed.</summary>
    public int RemoveKeysAt(int frame)
    {
        var removed = 0;
        foreach (var bone in _keys.Keys.ToList()) if (RemoveKey(bone, frame)) removed++;
        return removed;
    }

    /// <summary>Moves the keys at <paramref name="from"/> to <paramref name="to"/> (for the given bones, or all).</summary>
    public int MoveKeys(int from, int to, IEnumerable<string>? bones = null)
    {
        if (from == to) return 0;
        var moved = 0;
        foreach (var bone in (bones ?? _keys.Keys).ToList())
        {
            if (!_keys.TryGetValue(bone, out var k) || !k.TryGetValue(from, out var d)) continue;
            k.Remove(from);
            k[to] = d;
            moved++;
        }
        return moved;
    }

    public void ClearBone(string bone) => _keys.Remove(bone);
    public void Clear() => _keys.Clear();

    public KeyLayer Clone()
    {
        var c = new KeyLayer { FalloffFrames = FalloffFrames };
        foreach (var (bone, keys) in _keys) c._keys[bone] = new SortedDictionary<int, XForm>(keys);
        return c;
    }

    /// <summary>The offset this layer applies to <paramref name="bone"/> at <paramref name="frame"/>.</summary>
    public XForm Evaluate(string bone, float frame)
    {
        if (!_keys.TryGetValue(bone, out var k) || k.Count == 0) return XForm.Identity;
        int? prev = null, next = null;
        foreach (var f in k.Keys)
        {
            if (f <= frame) prev = f;
            if (f >= frame) { next = f; break; }
        }
        if (prev is int p && next is int n)
        {
            if (p == n) return k[p];
            var t = Smooth((frame - p) / (n - p));
            return Blend(k[p], k[n], t);
        }
        var falloff = Math.Max(1, FalloffFrames);
        if (next is int first) // before the first key
        {
            var w = 1f - (first - frame) / falloff;
            return w <= 0f ? XForm.Identity : Blend(XForm.Identity, k[first], Smooth(w));
        }
        var last = prev!.Value; // after the last key
        var wl = 1f - (frame - last) / falloff;
        return wl <= 0f ? XForm.Identity : Blend(XForm.Identity, k[last], Smooth(wl));
    }

    /// <summary>Returns new frames with every key applied (the input is not modified).</summary>
    public List<XForm[]> Apply(IReadOnlyList<XForm[]> frames, Rig.Skeleton skeleton)
    {
        var result = AnimClip.CopyFrames(frames);
        if (IsEmpty) return result;
        var bones = _keys.Keys.Select(skeleton.IndexOf).Where(i => i >= 0).ToArray();
        for (var f = 0; f < result.Count; f++)
            foreach (var b in bones)
            {
                var d = Evaluate(skeleton[b].Name, f);
                ref var local = ref result[f][b];
                local = ApplyDelta(local, d);
            }
        return result;
    }

    /// <summary>Applies an offset to a parent-local transform.</summary>
    public static XForm ApplyDelta(XForm local, XForm delta)
        => new(local.Pos + delta.Pos, MathQ.Normalize(delta.Rot * local.Rot));

    /// <summary>The offset that turns <paramref name="baseLocal"/> into <paramref name="targetLocal"/>.</summary>
    public static XForm DeltaBetween(XForm baseLocal, XForm targetLocal)
        => new(targetLocal.Pos - baseLocal.Pos, MathQ.Normalize(targetLocal.Rot * Quaternion.Conjugate(baseLocal.Rot)));

    static XForm Blend(XForm a, XForm b, float t)
        => new(Vector3.Lerp(a.Pos, b.Pos, t), MathQ.Normalize(Quaternion.Slerp(a.Rot, b.Rot, t)));

    static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // ---- serialization helpers (used by the workspace store) ----

    internal IEnumerable<(string Bone, int Frame, XForm Delta)> Enumerate()
    {
        foreach (var (bone, keys) in _keys)
            foreach (var (frame, d) in keys)
                yield return (bone, frame, d);
    }
}
