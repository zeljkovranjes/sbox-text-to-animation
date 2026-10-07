#nullable enable annotations

using System.Numerics;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>
/// Makes generated motion honour what the user asked to keep, exactly. Models hold pinned poses and locked
/// bones only approximately (they are re-encoded through the model's feature space), so after generation the
/// pinned frames are restored bit-for-bit (the small correction fades out over neighbouring frames so nothing
/// pops) and locked bones get their source local transforms back on every frame.
/// </summary>
public static class GenerationConstraints
{
    /// <summary>
    /// Restores <paramref name="source"/>'s full pose at each pinned frame. The difference to the generated
    /// pose is blended out over up to <paramref name="blendFrames"/> frames on each side, never past a
    /// neighbouring pin.
    /// </summary>
    public static void RestorePins(List<XForm[]> frames, IReadOnlyList<XForm[]> source, IEnumerable<int> pins, int blendFrames = 6)
    {
        var sorted = pins.Where(p => p >= 0 && p < frames.Count && p < source.Count).Distinct().OrderBy(p => p).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var p = sorted[i];
            var lo = Math.Max(i > 0 ? sorted[i - 1] + 1 : 0, p - blendFrames);
            var hi = Math.Min(i + 1 < sorted.Count ? sorted[i + 1] - 1 : frames.Count - 1, p + blendFrames);
            var target = source[p];
            var generated = frames[p];
            var bones = Math.Min(target.Length, generated.Length);
            var dPos = new Vector3[bones];
            var dRot = new Quaternion[bones];
            for (var b = 0; b < bones; b++)
            {
                dPos[b] = target[b].Pos - generated[b].Pos;
                var d = MathQ.Normalize(target[b].Rot * Quaternion.Conjugate(generated[b].Rot));
                dRot[b] = d.W < 0 ? -d : d; // shortest way
            }
            for (var f = lo; f <= hi; f++)
            {
                if (f == p) { frames[f] = CopyPose(target); continue; }
                var t = 1f - MathF.Abs(f - p) / (float)(blendFrames + 1);
                var w = t * t * (3f - 2f * t);
                var pose = CopyPose(frames[f]);
                for (var b = 0; b < Math.Min(bones, pose.Length); b++)
                {
                    pose[b].Pos += dPos[b] * w;
                    pose[b].Rot = MathQ.Normalize(Quaternion.Slerp(Quaternion.Identity, dRot[b], w) * pose[b].Rot);
                }
                frames[f] = pose;
            }
        }
    }

    /// <summary>Gives the <paramref name="bones"/> their <paramref name="source"/> local transforms on every frame.</summary>
    public static void RestoreBones(List<XForm[]> frames, IReadOnlyList<XForm[]> source, IEnumerable<int> bones)
    {
        if (source.Count == 0) return;
        var list = bones.Distinct().ToList();
        if (list.Count == 0) return;
        for (var f = 0; f < frames.Count; f++)
        {
            var src = source[Math.Min(f, source.Count - 1)];
            var pose = CopyPose(frames[f]);
            foreach (var b in list)
                if (b >= 0 && b < pose.Length && b < src.Length) pose[b] = src[b];
            frames[f] = pose;
        }
    }

    static XForm[] CopyPose(XForm[] pose)
    {
        var copy = new XForm[pose.Length];
        Array.Copy(pose, copy, pose.Length);
        return copy;
    }
}
