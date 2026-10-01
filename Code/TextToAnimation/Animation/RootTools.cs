#nullable enable annotations

using System.Numerics;
using TextToAnimation.Maths;
using TextToAnimation.Processing;

namespace TextToAnimation.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>
/// Reads and writes the motion root of a clip in world space and splits it into the ground-plane part
/// (horizontal travel + heading/yaw about up) and the rest. All root-motion tools build on this.
/// </summary>
public static class RootTools
{
    /// <summary>World transform of the motion root at a frame.</summary>
    public static XForm RootWorld(IReadOnlyList<XForm[]> frames, MotionRig rig, int frame)
        => FkUtil.BoneWorld(frames[frame], rig.Skeleton, rig.RootIndex);

    /// <summary>Writes a world transform to the motion root, keeping its children's world transforms
    /// consistent with the new root (they are parent-relative, so they simply follow).</summary>
    public static void SetRootWorld(List<XForm[]> frames, MotionRig rig, int frame, XForm world)
    {
        var parent = rig.Skeleton[rig.RootIndex].ParentIndex;
        frames[frame][rig.RootIndex] = parent < 0
            ? world
            : XForm.ToLocal(FkUtil.BoneWorld(frames[frame], rig.Skeleton, parent), world);
    }

    /// <summary>Heading (radians, about <see cref="MotionRig.Up"/>) of a world rotation, measured from the rig's forward.</summary>
    public static float Heading(MotionRig rig, Quaternion worldRotation, Quaternion restRotation)
    {
        // Direction the rest forward points after the bone's rotation delta from rest.
        var delta = worldRotation * Quaternion.Conjugate(restRotation);
        var f = Vector3.Transform(rig.Forward, delta);
        f -= Vector3.Dot(f, rig.Up) * rig.Up;
        if (f.LengthSquared() < 1e-8f) return 0f;
        return MathF.Atan2(Vector3.Dot(f, rig.Lateral), Vector3.Dot(f, rig.Forward));
    }

    /// <summary>Heading of the root at a frame, relative to the root's rest orientation.</summary>
    public static float RootHeading(IReadOnlyList<XForm[]> frames, MotionRig rig, int frame)
        => Heading(rig, RootWorld(frames, rig, frame).Rot, rig.Skeleton.RestWorld[rig.RootIndex].Rot);

    /// <summary>Ground-plane component of a position.</summary>
    public static Vector3 Horizontal(MotionRig rig, Vector3 p) => p - Vector3.Dot(p, rig.Up) * rig.Up;

    /// <summary>Rotation about the rig's up axis.</summary>
    public static Quaternion Yaw(MotionRig rig, float radians) => Quaternion.CreateFromAxisAngle(rig.Up, radians);

    /// <summary>
    /// The ground-plane frame of the root at a frame: horizontal position and heading. Transforming a
    /// clip by <c>B * inverse(A)</c> of two such frames moves it rigidly over the ground.
    /// </summary>
    public static XForm GroundFrame(IReadOnlyList<XForm[]> frames, MotionRig rig, int frame)
    {
        var world = RootWorld(frames, rig, frame);
        return new XForm(Horizontal(rig, world.Pos), Yaw(rig, RootHeading(frames, rig, frame)));
    }

    /// <summary>
    /// Rigidly moves frames [start, end] over the ground by <paramref name="transform"/> (a ground-plane
    /// rigid transform: horizontal translation + yaw), applied in world space to the root.
    /// </summary>
    public static void TransformRange(List<XForm[]> frames, MotionRig rig, int start, int end, XForm transform)
    {
        for (var f = start; f <= end; f++)
        {
            var w = RootWorld(frames, rig, f);
            var moved = XForm.Compose(transform, w);
            SetRootWorld(frames, rig, f, moved);
        }
    }

    /// <summary>World positions of the root over the whole clip (trajectory preview).</summary>
    public static Vector3[] Trajectory(IReadOnlyList<XForm[]> frames, MotionRig rig)
    {
        var path = new Vector3[frames.Count];
        for (var f = 0; f < frames.Count; f++) path[f] = RootWorld(frames, rig, f).Pos;
        return path;
    }

    /// <summary>World positions of the hips over the clip (falls back to the root).</summary>
    public static Vector3[] HipsTrajectory(IReadOnlyList<XForm[]> frames, MotionRig rig)
    {
        var bone = rig.HipsIndex >= 0 ? rig.HipsIndex : rig.RootIndex;
        var path = new Vector3[frames.Count];
        for (var f = 0; f < frames.Count; f++) path[f] = FkUtil.BoneWorld(frames[f], rig.Skeleton, bone).Pos;
        return path;
    }
}
