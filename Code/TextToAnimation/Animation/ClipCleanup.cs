#nullable enable annotations

using System.Numerics;
using TextToAnimation.Maths;
using TextToAnimation.Processing;

namespace TextToAnimation.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)
using Vector4 = System.Numerics.Vector4;
using Quaternion = System.Numerics.Quaternion;

/// <summary>
/// Motion clean-up passes from humanoid-retargeter applied to a workspace clip: foot-skate removal,
/// grounding, footstep events and mirroring. All mutate the clip in place.
/// </summary>
public static class ClipCleanup
{
    /// <summary>Locks planted feet in place (Kovar-style two-bone IK, from humanoid-retargeter).</summary>
    public static FootPlantReport? CleanFootSliding(AnimClip clip, MotionRig rig)
    {
        if (rig.LeftFoot is null || rig.RightFoot is null) return null;
        var report = FootPlant.Apply(clip.Frames, rig.Skeleton, rig.LeftFoot, rig.RightFoot, rig.Up, clip.Fps, rig.PlantOptions(clip.Fps));
        ClipOps.Finish(clip, rig);
        return report;
    }

    /// <summary>
    /// Cleans generated motion (UniMate's raw output jitters on rigs unlike its training skeletons, and its planted
    /// feet drift at about a fifth of the body's speed, as in UniMate itself): a light temporal smoothing (Gaussian,
    /// sigma 1.5 frames, on every bone's local transform), then planted feet locked to the floor with plants detected
    /// relative to the clip's own travel speed. Returns what it did.
    /// </summary>
    public static string CleanGenerated(List<XForm[]> frames, MotionRig rig, float fps, IReadOnlyList<int>? seams = null)
    {
        if (frames.Count < 5) return "";
        // where generation windows were chained: continuous over a third of a second
        if (seams is { Count: > 0 }) BlendSeams(frames, seams, Math.Max(2, (int)MathF.Round(fps / 3f)));
        var smoothed = Smooth(frames, 1.5f);
        for (var f = 0; f < frames.Count; f++) frames[f] = smoothed[f];
        var feet = rig.Feet.Count > 0 ? rig.Feet.ToList() : new List<FootChain>();
        if (rig.IsHumanoid && rig.LeftFoot is not null && rig.RightFoot is not null) feet = new List<FootChain> { rig.LeftFoot, rig.RightFoot };
        if (feet.Count == 0) return "Smoothed.";
        var body = rig.HipsIndex >= 0 ? rig.HipsIndex : rig.RootIndex;
        var world = new XForm[rig.Skeleton.Count];
        FkUtil.ToWorld(frames[0], rig.Skeleton, world);
        var start = world[body].Pos;
        FkUtil.ToWorld(frames[^1], rig.Skeleton, world);
        var travel = world[body].Pos - start;
        travel -= Vector3.Dot(travel, rig.Up) * rig.Up;
        var bodySpeed = travel.Length() / ((frames.Count - 1) / fps);
        var options = rig.PlantOptions(fps);
        // a generated foot is "planted" when it moves much slower than the body (never perfectly still)
        options.SpeedThresholdCmPerSec = MathF.Max(options.SpeedThresholdCmPerSec, 0.6f * bodySpeed);
        options.HeightThresholdCm *= 2.5f;
        // generated motion keeps every bone's length: a plant the leg can't reach stays where the leg ends
        options.MaxStretch = 0f;
        // every foot: locking only two of a quadruped's (its "main" pair) left the others sliding out of step
        int plants;
        if (feet.Count == 2 && rig.IsHumanoid)
        {
            var report = FootPlant.Apply(frames, rig.Skeleton, feet[0], feet[1], rig.Up, fps, options);
            plants = report.Left.Plants.Count + report.Right.Plants.Count;
        }
        else
        {
            // a creature's generated feet often glide rather than stand (UniMate on rigs unlike its data): only feet
            // that really stand are held, so a leg is never dragged into another
            options.MaxPlantDrift = PlantDrift;
            plants = FootPlant.ApplyAll(frames, rig.Skeleton, feet, rig.Up, fps, options).Sum(f => f.Plants.Count);
        }
        return plants > 0 ? $"Smoothed; {plants} foot plants locked." : "Smoothed.";
    }

    /// <summary>
    /// Removes the jump where chained generation windows meet (UniMate's expansion pins the overlap, but the first new
    /// frame need not continue the motion): at each seam, the gap between the new frame and the previous motion carried
    /// one frame on is added to the new side and fades out over <paramref name="blendFrames"/> (inertialization), so
    /// pose stays continuous and the new segment's own motion takes over. Bone offsets keep their lengths.
    /// </summary>
    public static void BlendSeams(List<XForm[]> frames, IEnumerable<int> seams, int blendFrames)
    {
        foreach (var s in seams)
        {
            if (s < 2 || s >= frames.Count || blendFrames < 1) continue;
            XForm[] a = frames[s - 2], b = frames[s - 1], c = frames[s];
            var bones = c.Length;
            var offPos = new Vector3[bones];
            var offRot = new Quaternion[bones];
            for (var k = 0; k < bones; k++)
            {
                var pos = b[k].Pos + (b[k].Pos - a[k].Pos);
                var rot = Quaternion.Normalize(Quaternion.Normalize(b[k].Rot * Quaternion.Inverse(a[k].Rot)) * b[k].Rot);
                offPos[k] = pos - c[k].Pos;
                var off = Quaternion.Normalize(rot * Quaternion.Inverse(c[k].Rot));
                offRot[k] = off.W < 0 ? Quaternion.Negate(off) : off;
            }
            for (var t = 0; t < blendFrames && s + t < frames.Count; t++)
            {
                var w = 0.5f * (1f + MathF.Cos(MathF.PI * t / blendFrames));
                var f = frames[s + t];
                var o = new XForm[bones];
                for (var k = 0; k < bones; k++)
                    o[k] = new XForm(f[k].Pos + offPos[k] * w, Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, offRot[k], w) * f[k].Rot));
                frames[s + t] = o;
            }
        }
    }

    /// <summary>How far (a fraction of the leg's length) a creature's foot may stray during a plant for it to be held.</summary>
    public const float PlantDrift = 0.15f;

    /// <summary>Gaussian smoothing of every bone's local transform over time (edges clamped; rotations averaged in one hemisphere).</summary>
    public static List<XForm[]> Smooth(IReadOnlyList<XForm[]> frames, float sigma)
    {
        var n = frames.Count; var bones = frames[0].Length; var r = (int)MathF.Ceiling(3 * sigma);
        var weights = new float[2 * r + 1];
        for (var k = -r; k <= r; k++) weights[k + r] = MathF.Exp(-k * k / (2 * sigma * sigma));
        var result = new List<XForm[]>(n);
        for (var f = 0; f < n; f++)
        {
            var o = new XForm[bones];
            for (var b = 0; b < bones; b++)
            {
                var q0 = frames[f][b].Rot; var acc = Vector4.Zero; var p = Vector3.Zero; var total = 0f;
                for (var k = -r; k <= r; k++)
                {
                    var src = frames[Math.Clamp(f + k, 0, n - 1)][b];
                    var q = Quaternion.Dot(src.Rot, q0) < 0 ? Quaternion.Negate(src.Rot) : src.Rot;
                    var w = weights[k + r];
                    acc += w * new Vector4(q.X, q.Y, q.Z, q.W); p += w * src.Pos; total += w;
                }
                var qn = Vector4.Normalize(acc);
                o[b] = new XForm(p / total, new Quaternion(qn.X, qn.Y, qn.Z, qn.W));
            }
            result.Add(o);
        }
        return result;
    }

    /// <summary>Moves the whole clip up/down so the feet rest on the floor the model stands on.</summary>
    public static float GroundFeet(AnimClip clip, MotionRig rig)
    {
        if (rig.LeftFoot is null || rig.RightFoot is null) return 0f;
        var restGround = MathF.Min(Vector3.Dot(rig.Skeleton.RestWorld[rig.LeftFoot.Ankle].Pos, rig.Up),
                                   Vector3.Dot(rig.Skeleton.RestWorld[rig.RightFoot.Ankle].Pos, rig.Up));
        var lowest = new List<float>(clip.FrameCount);
        var world = new XForm[rig.Skeleton.Count];
        foreach (var frame in clip.Frames)
        {
            FkUtil.ToWorld(frame, rig.Skeleton, world);
            lowest.Add(MathF.Min(Vector3.Dot(world[rig.LeftFoot.Ankle].Pos, rig.Up), Vector3.Dot(world[rig.RightFoot.Ankle].Pos, rig.Up)));
        }
        lowest.Sort();
        var shift = restGround - lowest[lowest.Count / 10];
        if (MathF.Abs(shift) < rig.Cm(0.2f)) return 0f;
        for (var f = 0; f < clip.FrameCount; f++)
        {
            var w = RootTools.RootWorld(clip.Frames, rig, f);
            RootTools.SetRootWorld(clip.Frames, rig, f, new XForm(w.Pos + rig.Up * shift, w.Rot));
        }
        ClipOps.Finish(clip, rig);
        return shift;
    }

    /// <summary>Replaces the clip's automatic footstep events with freshly detected ones.</summary>
    public static int GenerateFootsteps(AnimClip clip, MotionRig rig)
    {
        clip.Events.RemoveAll(e => e.Automatic);
        if (rig.LeftFoot is null || rig.RightFoot is null || clip.FrameCount < 4) return 0;
        var frames = clip.EvaluateFrames(rig.Skeleton);
        var events = FootstepEvents.Generate(frames, rig.Skeleton, rig.LeftFoot, rig.RightFoot, rig.Up, clip.Fps, rig.PlantOptions(clip.Fps));
        foreach (var e in events)
            clip.Events.Add(new ClipEvent
            {
                EventClass = e.EventClass, Frame = e.Frame, Attachment = e.Attachment, Foot = e.Foot, Volume = e.Volume, Automatic = true,
            });
        clip.Events.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        return events.Count;
    }

    /// <summary>Returns mirrored frames (left/right swapped) for a mirrored variant, or null if the rig isn't symmetric.</summary>
    public static List<XForm[]>? MirrorFrames(IReadOnlyList<XForm[]> frames, MotionRig rig, out string? error)
    {
        error = null;
        if (rig.HasNamedRoles)
        {
            try { return ClipMirror.Mirror(frames.ToList(), rig.Rig); }
            catch (ArgumentException) { /* names don't pair up: use the shape below */ }
        }
        return MirrorFramesByShape(frames, rig, out error);
    }

    /// <summary>Mirrors using left/right partners and the mirror plane found from the skeleton's shape (any bone names).</summary>
    public static List<XForm[]>? MirrorFramesByShape(IReadOnlyList<XForm[]> frames, MotionRig rig, out string? error)
    {
        error = null;
        try
        {
            var a = rig.Analysis;
            if (!a.Symmetric)
            {
                error = "This skeleton has no left and right sides to swap, so it can't be mirrored.";
                return null;
            }
            // locals mirror correctly only across a plane through the model's origin
            if (MathF.Abs(a.MirrorOffset) > 0.01f * MathF.Max(a.Size, 1e-3f))
            {
                error = "This skeleton isn't centred on its mirror plane, so it can't be mirrored.";
                return null;
            }
            return ClipMirror.Mirror(frames.ToList(), rig.Skeleton, a.MirrorNormal, a.MirrorAll);
        }
        catch (ArgumentException ex) { error = ex.Message; return null; }
    }
}
