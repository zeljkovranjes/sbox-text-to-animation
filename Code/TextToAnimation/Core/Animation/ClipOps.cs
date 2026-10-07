#nullable enable annotations

using System.Numerics;
using TextToAnimation.Core.Formats;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.Core.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>
/// Clip editing operations. Every operation mutates the clip in place (take an undo snapshot first),
/// keeps events / pinned frames / keys on the right frames, keeps quaternions continuous, keeps root
/// travel continuous across splices and re-bakes derived helper bones (s&amp;box IK targets).
/// Frame ranges are inclusive.
/// </summary>
public static class ClipOps
{
    // ------------------------------------------------------------------ structure

    /// <summary>Keeps only frames [start, end] (crop/trim).</summary>
    public static void Crop(AnimClip clip, MotionRig rig, int start, int end)
    {
        CheckRange(clip, start, end);
        clip.Frames = clip.Frames.GetRange(start, end - start + 1);
        Remap(clip, f => f >= start && f <= end ? f - start : null);
        Finish(clip, rig);
    }

    /// <summary>Removes the first <paramref name="headFrames"/> and last <paramref name="tailFrames"/> frames.</summary>
    public static void Trim(AnimClip clip, MotionRig rig, int headFrames, int tailFrames)
        => Crop(clip, rig, headFrames, clip.FrameCount - 1 - tailFrames);

    /// <summary>Removes frames [start, end] and joins the rest so root travel continues smoothly.</summary>
    public static void DeleteSection(AnimClip clip, MotionRig rig, int start, int end)
    {
        CheckRange(clip, start, end);
        if (end - start + 1 >= clip.FrameCount - 1)
            throw new InvalidOperationException("Can't delete the whole clip - at least two frames must remain.");
        var before = clip.Frames.GetRange(0, start);
        var after = clip.Frames.GetRange(end + 1, clip.FrameCount - end - 1);
        var count = end - start + 1;
        clip.Frames = Splice(rig, before, after);
        Remap(clip, f => f < start ? f : f > end ? f - count : null);
        Finish(clip, rig);
    }

    /// <summary>Inserts a copy of frames [start, end] right after <paramref name="end"/>; the copy continues the root path.</summary>
    public static void DuplicateSection(AnimClip clip, MotionRig rig, int start, int end)
    {
        CheckRange(clip, start, end);
        var head = clip.Frames.GetRange(0, end + 1);
        var copy = AnimClip.CopyFrames(clip.Frames.GetRange(start, end - start + 1));
        var tail = clip.Frames.GetRange(end + 1, clip.FrameCount - end - 1);
        var count = end - start + 1;
        var joined = Splice(rig, head, copy);
        clip.Frames = Splice(rig, joined, tail);
        // events/pins/keys on the duplicated span are copied too
        var copiedEvents = clip.Events.Where(e => e.Frame >= start && e.Frame <= end)
            .Select(e => { var c = e.Clone(); c.Frame = e.Frame - start + end + 1; return c; }).ToList();
        Remap(clip, f => f <= end ? f : f + count);
        clip.Events.AddRange(copiedEvents);
        clip.Events.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        Finish(clip, rig);
    }

    /// <summary>Splits at <paramref name="frame"/>: the clip keeps [0, frame], the returned clip holds [frame, end].</summary>
    public static AnimClip Split(AnimClip clip, MotionRig rig, int frame, string secondName)
    {
        if (frame <= 0 || frame >= clip.FrameCount - 1)
            throw new ArgumentOutOfRangeException(nameof(frame), "Split point must be inside the clip.");
        var second = clip.Duplicate(secondName);
        Crop(second, rig, frame, second.FrameCount - 1);
        Crop(clip, rig, 0, frame);
        clip.Looping = false;
        second.Looping = false;
        return second;
    }

    /// <summary>Plays the clip backwards.</summary>
    public static void Reverse(AnimClip clip, MotionRig rig)
    {
        var n = clip.FrameCount;
        clip.Frames.Reverse();
        Remap(clip, f => n - 1 - f);
        foreach (var e in clip.Events.Where(e => e.Automatic).ToList()) clip.Events.Remove(e); // footsteps become wrong: regenerate
        Finish(clip, rig);
    }

    /// <summary>Plays frames [start, end] backwards in place; the rest of the clip continues the root path.</summary>
    public static void ReverseSection(AnimClip clip, MotionRig rig, int start, int end)
    {
        CheckRange(clip, start, end);
        if (start == 0 && end == clip.FrameCount - 1) { Reverse(clip, rig); return; }
        if (end - start < 1) return;
        var head = clip.Frames.GetRange(0, start);
        var middle = AnimClip.CopyFrames(clip.Frames.GetRange(start, end - start + 1));
        middle.Reverse();
        var tail = clip.Frames.GetRange(end + 1, clip.FrameCount - end - 1);
        clip.Frames = Splice(rig, Splice(rig, head, middle), tail);
        foreach (var e in clip.Events.Where(e => e.Automatic && e.Frame >= start && e.Frame <= end).ToList()) clip.Events.Remove(e);
        Remap(clip, f => f >= start && f <= end ? start + end - f : f);
        Finish(clip, rig);
    }

    /// <summary>Changes the sample rate, keeping the duration (motion is resampled).</summary>
    public static void Resample(AnimClip clip, MotionRig rig, float newFps)
    {
        if (!(newFps > 0f) || !float.IsFinite(newFps)) throw new ArgumentOutOfRangeException(nameof(newFps));
        if (MathF.Abs(newFps - clip.Fps) < 1e-4f) return;
        var duration = clip.Duration;
        var count = Math.Max(2, (int)MathF.Round(duration * newFps) + 1);
        ResampleTo(clip, rig, count, newFps);
    }

    /// <summary>Speeds the motion up (factor &gt; 1) or slows it down (factor &lt; 1) at the same frame rate.</summary>
    public static void TimeScale(AnimClip clip, MotionRig rig, float factor)
    {
        if (!(factor > 0f) || !float.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor));
        var count = Math.Max(2, (int)MathF.Round((clip.FrameCount - 1) / factor) + 1);
        ResampleTo(clip, rig, count, clip.Fps);
    }

    /// <summary>Samples the clip at a fractional frame (linear position, slerped rotation).</summary>
    public static XForm[] Sample(IReadOnlyList<XForm[]> frames, float frame)
    {
        if (frames.Count == 0) throw new InvalidOperationException("Clip has no frames.");
        frame = Math.Clamp(frame, 0f, frames.Count - 1);
        var a = (int)MathF.Floor(frame);
        var b = Math.Min(a + 1, frames.Count - 1);
        var t = frame - a;
        var fa = frames[a];
        if (t <= 1e-6f || a == b) { var copy = new XForm[fa.Length]; Array.Copy(fa, copy, fa.Length); return copy; }
        var fb = frames[b];
        var result = new XForm[fa.Length];
        for (var i = 0; i < fa.Length; i++)
            result[i] = new XForm(Vector3.Lerp(fa[i].Pos, fb[i].Pos, t), MathQ.Normalize(Quaternion.Slerp(fa[i].Rot, fb[i].Rot, t)));
        return result;
    }

    static void ResampleTo(AnimClip clip, MotionRig rig, int count, float fps)
    {
        var old = clip.Frames;
        var scale = (old.Count - 1) / (float)(count - 1);
        var frames = new List<XForm[]>(count);
        for (var f = 0; f < count; f++) frames.Add(Sample(old, f * scale));
        clip.Frames = frames;
        clip.Fps = fps;
        Remap(clip, f => Math.Clamp((int)MathF.Round(f / scale), 0, count - 1));
        Finish(clip, rig);
    }

    // ------------------------------------------------------------------ root motion

    /// <summary>Removes horizontal travel: the character stays over its start position.</summary>
    public static void MakeInPlace(AnimClip clip, MotionRig rig)
    {
        if (rig.HipsIndex < 0) throw new InvalidOperationException("This skeleton has no hips bone to keep in place.");
        // The travel is the hips' ground path with the natural sway smoothed out (a quarter second window).
        // The whole body is moved back by it via the motion root, so rigs with a separate root bone and rigs
        // where the hips are the root behave the same.
        var path = RootTools.HipsTrajectory(clip.Frames, rig).Select(p => RootTools.Horizontal(rig, p)).ToArray();
        var travel = SmoothPath(path, Math.Max(1, (int)MathF.Round(clip.Fps * 0.25f)));
        for (var f = 0; f < clip.FrameCount; f++)
        {
            var w = RootTools.RootWorld(clip.Frames, rig, f);
            RootTools.SetRootWorld(clip.Frames, rig, f, new XForm(w.Pos - (travel[f] - travel[0]), w.Rot));
        }
        clip.Export.RootMotion = ClipRootMotion.InPlace;
        Finish(clip, rig);
    }

    /// <summary>Centered moving average with point-reflected (linearly extrapolated) edges, so a straight
    /// path is reproduced exactly - including at the first and last frame.</summary>
    internal static Vector3[] SmoothPath(Vector3[] path, int halfWindow)
    {
        var n = path.Length;
        var result = new Vector3[n];
        if (n == 0) return result;
        Vector3 At(int i) => i < 0 ? 2f * path[0] - path[Math.Min(-i, n - 1)]
            : i >= n ? 2f * path[n - 1] - path[Math.Max(2 * (n - 1) - i, 0)] : path[i];
        for (var i = 0; i < n; i++)
        {
            var sum = Vector3.Zero;
            for (var k = -halfWindow; k <= halfWindow; k++) sum += At(i + k);
            result[i] = sum / (2 * halfWindow + 1);
        }
        return result;
    }

    /// <summary>
    /// Removes slow unwanted drift: the straight-line horizontal offset (and heading change) between the
    /// first and last frame is distributed out over the clip, so it starts and ends at the same spot
    /// facing the same way. Meant for idles and in-place loops.
    /// </summary>
    public static void RemoveRootDrift(AnimClip clip, MotionRig rig, bool position = true, bool heading = true)
    {
        var n = clip.FrameCount;
        if (n < 2) return;
        var start = RootTools.GroundFrame(clip.Frames, rig, 0);
        var end = RootTools.GroundFrame(clip.Frames, rig, n - 1);
        var dPos = end.Pos - start.Pos;
        var dYaw = WrapAngle(RootTools.RootHeading(clip.Frames, rig, n - 1) - RootTools.RootHeading(clip.Frames, rig, 0));
        for (var f = 0; f < n; f++)
        {
            var t = f / (float)(n - 1);
            var correctionYaw = heading ? RootTools.Yaw(rig, -dYaw * t) : Quaternion.Identity;
            var pivot = RootTools.GroundFrame(clip.Frames, rig, f).Pos;
            var w = RootTools.RootWorld(clip.Frames, rig, f);
            // rotate about the root's own ground position so heading changes don't swing it around
            var p = pivot + Vector3.Transform(w.Pos - pivot, correctionYaw);
            if (position) p -= dPos * t;
            RootTools.SetRootWorld(clip.Frames, rig, f, new XForm(p, MathQ.Normalize(correctionYaw * w.Rot)));
        }
        Finish(clip, rig);
    }

    /// <summary>
    /// Moves/turns the whole clip over the ground. With <paramref name="progressive"/> the offset grows
    /// from zero at the first frame to the full value at the last (bends or lengthens the path instead).
    /// </summary>
    public static void OffsetRoot(AnimClip clip, MotionRig rig, Vector3 translation, float yawDegrees, bool progressive)
    {
        var n = clip.FrameCount;
        var pivot = RootTools.GroundFrame(clip.Frames, rig, 0).Pos;
        for (var f = 0; f < n; f++)
        {
            var t = progressive ? (n == 1 ? 1f : f / (float)(n - 1)) : 1f;
            var yaw = RootTools.Yaw(rig, yawDegrees * t * MathF.PI / 180f);
            var w = RootTools.RootWorld(clip.Frames, rig, f);
            var p = pivot + Vector3.Transform(w.Pos - pivot, yaw) + translation * t;
            RootTools.SetRootWorld(clip.Frames, rig, f, new XForm(p, MathQ.Normalize(yaw * w.Rot)));
        }
        Finish(clip, rig);
    }

    /// <summary>Moves the root so the clip starts at the origin facing the rig's forward.</summary>
    public static void ResetStart(AnimClip clip, MotionRig rig)
    {
        var start = RootTools.GroundFrame(clip.Frames, rig, 0);
        var restGround = RootTools.Horizontal(rig, rig.Skeleton.RestWorld[rig.RootIndex].Pos);
        var inv = start.Inverse();
        var toRest = XForm.Compose(new XForm(restGround, Quaternion.Identity), inv);
        RootTools.TransformRange(clip.Frames, rig, 0, clip.FrameCount - 1, toRest);
        Finish(clip, rig);
    }

    // ------------------------------------------------------------------ loops

    /// <summary>
    /// Makes the clip loop seamlessly: the pose difference between the last and the first frame is blended
    /// out over the last <paramref name="blendFrames"/> frames (horizontal travel and heading are kept, so a
    /// walk cycle still moves forward). The clip is marked looping.
    /// </summary>
    public static void MakeSeamlessLoop(AnimClip clip, MotionRig rig, int blendFrames)
    {
        var n = clip.FrameCount;
        if (n < 4) throw new InvalidOperationException("The clip is too short to loop.");
        blendFrames = Math.Clamp(blendFrames, 1, n - 2);
        var first = clip.Frames[0];
        var last = clip.Frames[n - 1];
        var bones = rig.Skeleton.Count;

        // Per-bone correction that turns the last pose into the first. For the motion root only the
        // non-ground part (height, tilt) is corrected: travel and heading stay authored.
        var corrections = new XForm[bones];
        for (var b = 0; b < bones; b++)
        {
            if (b == rig.RootIndex) continue;
            corrections[b] = KeyLayer.DeltaBetween(last[b], first[b]);
        }
        var firstRoot = RootTools.RootWorld(clip.Frames, rig, 0);
        var lastRoot = RootTools.RootWorld(clip.Frames, rig, n - 1);
        var firstGround = RootTools.GroundFrame(clip.Frames, rig, 0);
        var lastGround = RootTools.GroundFrame(clip.Frames, rig, n - 1);
        // the first root expressed relative to its ground frame, re-applied on the last ground frame
        var firstRelative = XForm.Compose(firstGround.Inverse(), firstRoot);
        var targetRoot = XForm.Compose(lastGround, firstRelative);
        var rootCorrection = new XForm(targetRoot.Pos - lastRoot.Pos, MathQ.Normalize(targetRoot.Rot * Quaternion.Conjugate(lastRoot.Rot)));

        for (var i = 0; i < blendFrames; i++)
        {
            var f = n - blendFrames + i;
            var t = (i + 1) / (float)blendFrames;
            var w = t * t * (3f - 2f * t);
            var frame = clip.Frames[f];
            for (var b = 0; b < bones; b++)
            {
                if (b == rig.RootIndex) continue;
                frame[b] = KeyLayer.ApplyDelta(frame[b], Scale(corrections[b], w));
            }
            var root = RootTools.RootWorld(clip.Frames, rig, f);
            RootTools.SetRootWorld(clip.Frames, rig, f, KeyLayer.ApplyDelta(root, Scale(rootCorrection, w)));
        }
        clip.Looping = true;
        Finish(clip, rig);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Re-derives helper bones and fixes quaternion continuity after any edit.</summary>
    public static void Finish(AnimClip clip, MotionRig rig)
    {
        if (rig.Rig.BonesOfClass(Rig.BoneClass.IkBaked).Any())
        {
            try { IkBoneBaker.Bake(clip.Frames, rig.Rig); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { /* rig without baked IK layout */ }
        }
        QuaternionContinuity.AlignFrames(clip.Frames);
        clip.PinnedFrames.RemoveWhere(f => f < 0 || f >= clip.FrameCount);
        clip.Events.RemoveAll(e => e.Frame < 0 || e.Frame >= clip.FrameCount);
        clip.Revision++;
    }

    /// <summary>Concatenates two frame lists, moving the second rigidly over the ground so its start
    /// continues from the first one's end (one frame of the previous velocity is kept).</summary>
    public static List<XForm[]> Splice(MotionRig rig, List<XForm[]> a, List<XForm[]> b)
    {
        var result = new List<XForm[]>(a.Count + b.Count);
        result.AddRange(a);
        if (b.Count == 0) return result;
        var copyB = AnimClip.CopyFrames(b);
        if (a.Count > 0)
        {
            var endA = RootTools.GroundFrame(a, rig, a.Count - 1);
            // step that the motion would have taken: velocity from the last two frames of A
            var step = a.Count >= 2
                ? RootTools.GroundFrame(a, rig, a.Count - 1).Pos - RootTools.GroundFrame(a, rig, a.Count - 2).Pos
                : Vector3.Zero;
            var startB = RootTools.GroundFrame(copyB, rig, 0);
            var target = new XForm(endA.Pos + step, endA.Rot);
            var transform = XForm.Compose(target, startB.Inverse());
            RootTools.TransformRange(copyB, rig, 0, copyB.Count - 1, transform);
        }
        result.AddRange(copyB);
        return result;
    }

    static XForm Scale(XForm delta, float w)
        => new(delta.Pos * w, MathQ.Normalize(Quaternion.Slerp(Quaternion.Identity, delta.Rot, w)));

    static RootMotionAxes Axes(MotionRig rig) => new()
    {
        Up = rig.Up,
        RootIndex = rig.RootIndex,
        HipsIndex = rig.HipsIndex >= 0 ? rig.HipsIndex : rig.RootIndex,
        HipsParentIsRoot = rig.Skeleton[rig.HipsIndex >= 0 ? rig.HipsIndex : rig.RootIndex].ParentIndex == rig.RootIndex,
    };

    static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    static void CheckRange(AnimClip clip, int start, int end)
    {
        if (start < 0 || end >= clip.FrameCount || start > end)
            throw new ArgumentOutOfRangeException(nameof(start), $"Frame range {start}-{end} is outside the clip (0-{clip.FrameCount - 1}).");
    }

    /// <summary>Moves events, pinned frames and keys through a frame mapping (null = removed).</summary>
    static void Remap(AnimClip clip, Func<int, int?> map)
    {
        foreach (var e in clip.Events.ToList())
        {
            if (map(e.Frame) is int f) e.Frame = f; else clip.Events.Remove(e);
        }
        clip.Events.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        clip.PinnedFrames = new SortedSet<int>(clip.PinnedFrames.Select(map).Where(f => f.HasValue).Select(f => f!.Value));
        var keys = clip.Keys.Enumerate().ToList();
        var layer = new KeyLayer { FalloffFrames = clip.Keys.FalloffFrames };
        foreach (var (bone, frame, delta) in keys)
            if (map(frame) is int f) layer.SetKey(bone, f, delta);
        clip.Keys = layer;
    }
}
