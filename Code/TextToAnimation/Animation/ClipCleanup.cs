#nullable enable annotations

using System.Numerics;
using TextToAnimation.Maths;
using TextToAnimation.Processing;

namespace TextToAnimation.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

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
