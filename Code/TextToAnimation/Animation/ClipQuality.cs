#nullable enable annotations

using System.Numerics;
using TextToAnimation.Formats;
using TextToAnimation.Maths;
using TextToAnimation.Processing;

namespace TextToAnimation.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

public enum IssueSeverity { Info, Warning, Error }

/// <summary>One-click fixes the quality panel can offer for an issue.</summary>
public enum IssueFix { None, FixRotations, CleanFootSliding, GroundFeet, MakeSeamlessLoop, RemoveRootDrift }

public sealed record ClipIssue(IssueSeverity Severity, string Code, string Message, int? Frame = null, string? Bone = null, IssueFix Fix = IssueFix.None);

/// <summary>
/// Detects the problems that make an animation look wrong in game: invalid numbers, quaternion flips,
/// rotation pops, wrong scale, flipped/lying axes, root teleports, foot sliding, floating or sinking feet
/// and bad loop seams. Thresholds are in centimeters / degrees and converted through the rig's units.
/// </summary>
public static class ClipQuality
{
    public static List<ClipIssue> Analyze(AnimClip clip, MotionRig rig)
    {
        var issues = new List<ClipIssue>();
        var frames = clip.EvaluateFrames(rig.Skeleton);
        var n = frames.Count;
        if (n == 0) { issues.Add(new(IssueSeverity.Error, "empty", "The clip has no frames.")); return issues; }
        if (frames.Any(f => f.Length != rig.Skeleton.Count))
        {
            issues.Add(new(IssueSeverity.Error, "bones", "The clip's bone count doesn't match the model's skeleton."));
            return issues;
        }

        foreach (var p in rig.Problems) issues.Add(new(IssueSeverity.Warning, "mapping", p));

        if (CheckFinite(frames, rig, issues)) return issues;
        CheckContinuity(frames, rig, clip.Fps, issues);
        CheckBoneLengths(frames, rig, issues);
        if (rig.HipsIndex >= 0 && rig.LeftFoot is not null && rig.RightFoot is not null)
        {
            CheckScaleAndAxes(frames, rig, issues);
            CheckFeet(frames, rig, clip.Fps, issues);
        }
        CheckRoot(frames, rig, clip.Fps, issues);
        if (clip.Looping) CheckLoopSeam(frames, rig, issues);
        foreach (var e in clip.Events.Where(e => e.Frame < 0 || e.Frame >= n))
            issues.Add(new(IssueSeverity.Warning, "event", $"Event {e.EventClass} at frame {e.Frame} is outside the clip.", e.Frame));
        return issues;
    }

    static bool CheckFinite(List<XForm[]> frames, MotionRig rig, List<ClipIssue> issues)
    {
        for (var f = 0; f < frames.Count; f++)
            for (var b = 0; b < frames[f].Length; b++)
            {
                var x = frames[f][b];
                if (!float.IsFinite(x.Pos.X + x.Pos.Y + x.Pos.Z + x.Rot.X + x.Rot.Y + x.Rot.Z + x.Rot.W)
                    || MathF.Abs(x.Rot.Length() - 1f) > 0.05f)
                {
                    issues.Add(new(IssueSeverity.Error, "invalid", $"Invalid transform on {rig.Skeleton[b].Name}.", f, rig.Skeleton[b].Name));
                    return true;
                }
            }
        return false;
    }

    static void CheckContinuity(List<XForm[]> frames, MotionRig rig, float fps, List<ClipIssue> issues)
    {
        var flips = 0;
        var maxDegPerSec = 0f; int spikeFrame = -1, spikeBone = -1;
        for (var f = 1; f < frames.Count; f++)
            for (var b = 0; b < rig.Skeleton.Count; b++)
            {
                if (!rig.IsMotionBone(b)) continue;
                var a = frames[f - 1][b].Rot; var c = frames[f][b].Rot;
                if (Quaternion.Dot(a, c) < 0f) flips++;
                var speed = MathQ.AngleBetween(a, c) * 180f / MathF.PI * fps;
                if (speed > maxDegPerSec) { maxDegPerSec = speed; spikeFrame = f; spikeBone = b; }
            }
        if (flips > 0)
            issues.Add(new(IssueSeverity.Info, "flip", $"{flips} quaternion sign flips (harmless to the pose, but can make blending spin). Fixed on save.", Fix: IssueFix.FixRotations));
        // 1500 deg/s is far beyond human joint speed except for very fast impacts
        if (maxDegPerSec > 1500f && spikeBone >= 0)
            issues.Add(new(IssueSeverity.Warning, "pop", $"{rig.Skeleton[spikeBone].Name} snaps {maxDegPerSec / fps:0}° in one frame - looks like a pop.", spikeFrame, rig.Skeleton[spikeBone].Name));
    }

    static void CheckBoneLengths(List<XForm[]> frames, MotionRig rig, List<ClipIssue> issues)
    {
        var worst = 0f; int worstBone = -1, worstFrame = -1;
        for (var b = 0; b < rig.Skeleton.Count; b++)
        {
            if (rig.Skeleton[b].ParentIndex < 0 || !rig.IsMotionBone(b)) continue;
            var rest = rig.Skeleton[b].RestLocal.Pos.Length();
            if (rest < rig.Cm(1f)) continue;
            for (var f = 0; f < frames.Count; f++)
            {
                var ratio = MathF.Abs(frames[f][b].Pos.Length() / rest - 1f);
                if (ratio > worst) { worst = ratio; worstBone = b; worstFrame = f; }
            }
        }
        if (worst > 0.25f)
            issues.Add(new(IssueSeverity.Warning, "stretch", $"{rig.Skeleton[worstBone].Name} is stretched {worst:P0} from its rest length - the animation may be at the wrong scale or for a different skeleton.", worstFrame, rig.Skeleton[worstBone].Name));
    }

    static void CheckScaleAndAxes(List<XForm[]> frames, MotionRig rig, List<ClipIssue> issues)
    {
        if (rig.HipHeight <= 0f) return;
        var heights = new List<float>();
        var upDots = new List<float>();
        var world = new XForm[rig.Skeleton.Count];
        var shoulderL = rig.Bone(Mapping.BoneRole.UpperArmL) ?? -1;
        var shoulderR = rig.Bone(Mapping.BoneRole.UpperArmR) ?? -1;
        foreach (var frame in frames)
        {
            FkUtil.ToWorld(frame, rig.Skeleton, world);
            var hips = world[rig.HipsIndex].Pos;
            var ground = MathF.Min(Vector3.Dot(world[rig.LeftFoot!.Ankle].Pos, rig.Up), Vector3.Dot(world[rig.RightFoot!.Ankle].Pos, rig.Up));
            heights.Add(Vector3.Dot(hips, rig.Up) - ground);
            if (shoulderL >= 0 && shoulderR >= 0)
            {
                var spine = (world[shoulderL].Pos + world[shoulderR].Pos) * 0.5f - hips;
                if (spine.LengthSquared() > 1e-6f) upDots.Add(Vector3.Dot(Vector3.Normalize(spine), rig.Up));
            }
        }
        var restAnkle = MathF.Min(Vector3.Dot(rig.Skeleton.RestWorld[rig.LeftFoot!.Ankle].Pos, rig.Up), Vector3.Dot(rig.Skeleton.RestWorld[rig.RightFoot!.Ankle].Pos, rig.Up));
        var restHeight = Vector3.Dot(rig.Skeleton.RestWorld[rig.HipsIndex].Pos, rig.Up) - restAnkle;
        heights.Sort();
        var maxHeight = heights[^1];
        if (restHeight > 0f && maxHeight > restHeight * 2.5f)
            issues.Add(new(IssueSeverity.Error, "scale", $"The hips rise to {maxHeight / restHeight:0.0}× the model's hip height - the animation looks too large for this model."));
        if (upDots.Count > 0)
        {
            var mean = upDots.Average();
            if (mean < -0.3f)
                issues.Add(new(IssueSeverity.Error, "axes", "The character is upside down for most of the clip - the animation's up axis is flipped."));
            else if (mean < 0.3f && upDots.Count(d => d < 0.3f) > upDots.Count * 0.9f)
                issues.Add(new(IssueSeverity.Warning, "axes", "The character lies on its side for the whole clip. If that isn't intended the axes are wrong."));
        }
    }

    static void CheckFeet(List<XForm[]> frames, MotionRig rig, float fps, List<ClipIssue> issues)
    {
        if (frames.Count < 4) return;
        var options = rig.PlantOptions(fps);
        List<FrameRange> left, right;
        try { (left, right) = FootPlant.DetectPlantIntervals(frames, rig.Skeleton, rig.LeftFoot!, rig.RightFoot!, rig.Up, fps, options); }
        catch (ArgumentException) { return; }
        var worstSlide = 0f; int slideFrame = -1;
        foreach (var (chain, plants) in new[] { (rig.LeftFoot!, left), (rig.RightFoot!, right) })
        {
            var ankle = FootPlant.AnkleWorldPositions(frames, rig.Skeleton, chain.Ankle);
            foreach (var plant in plants)
            {
                var anchor = RootTools.Horizontal(rig, ankle[plant.Start]);
                for (var f = plant.Start; f <= plant.End; f++)
                {
                    var slide = (RootTools.Horizontal(rig, ankle[f]) - anchor).Length();
                    if (slide > worstSlide) { worstSlide = slide; slideFrame = f; }
                }
            }
        }
        if (worstSlide > rig.Cm(4f))
            issues.Add(new(IssueSeverity.Warning, "slide", $"A planted foot slides {worstSlide / rig.UnitsPerCm:0} cm.", slideFrame, Fix: IssueFix.CleanFootSliding));

        // floating / sinking: compare the lowest foot point to the rest ground
        var restGround = MathF.Min(Vector3.Dot(rig.Skeleton.RestWorld[rig.LeftFoot!.Ankle].Pos, rig.Up), Vector3.Dot(rig.Skeleton.RestWorld[rig.RightFoot!.Ankle].Pos, rig.Up));
        var lowest = new List<float>();
        var world = new XForm[rig.Skeleton.Count];
        foreach (var frame in frames)
        {
            FkUtil.ToWorld(frame, rig.Skeleton, world);
            lowest.Add(MathF.Min(Vector3.Dot(world[rig.LeftFoot!.Ankle].Pos, rig.Up), Vector3.Dot(world[rig.RightFoot!.Ankle].Pos, rig.Up)));
        }
        lowest.Sort();
        var typical = lowest[lowest.Count / 10]; // 10th percentile: the feet are on the ground at least this often
        var offset = typical - restGround;
        if (offset > rig.Cm(6f))
            issues.Add(new(IssueSeverity.Warning, "float", $"Feet hover {offset / rig.UnitsPerCm:0} cm above the floor.", Fix: IssueFix.GroundFeet));
        else if (offset < -rig.Cm(6f))
            issues.Add(new(IssueSeverity.Warning, "sink", $"Feet sink {-offset / rig.UnitsPerCm:0} cm into the floor.", Fix: IssueFix.GroundFeet));
    }

    static void CheckRoot(List<XForm[]> frames, MotionRig rig, float fps, List<ClipIssue> issues)
    {
        var path = RootTools.HipsTrajectory(frames, rig);
        var worst = 0f; int at = -1;
        for (var f = 1; f < path.Length; f++)
        {
            var speed = (RootTools.Horizontal(rig, path[f] - path[f - 1])).Length() * fps;
            if (speed > worst) { worst = speed; at = f; }
        }
        // ~12 m/s is beyond a sprint: treat as a teleport
        if (worst > rig.Cm(1200f))
            issues.Add(new(IssueSeverity.Warning, "teleport", $"The root jumps {worst / fps / rig.UnitsPerCm:0} cm in a single frame.", at));
    }

    static void CheckLoopSeam(List<XForm[]> frames, MotionRig rig, List<ClipIssue> issues)
    {
        var first = frames[0]; var last = frames[^1];
        var worst = 0f; var worstBone = -1;
        for (var b = 0; b < rig.Skeleton.Count; b++)
        {
            if (!rig.IsMotionBone(b) || b == rig.RootIndex) continue;
            var a = MathQ.AngleBetween(first[b].Rot, last[b].Rot) * 180f / MathF.PI;
            if (a > worst) { worst = a; worstBone = b; }
        }
        if (worst > 8f)
            issues.Add(new(IssueSeverity.Warning, "seam", $"The loop pops: {rig.Skeleton[worstBone].Name} differs {worst:0}° between the last and first frame.", frames.Count - 1, rig.Skeleton[worstBone].Name, IssueFix.MakeSeamlessLoop));
    }

    /// <summary>Applies the one-click fix for an issue.</summary>
    public static void ApplyFix(AnimClip clip, MotionRig rig, IssueFix fix)
    {
        switch (fix)
        {
            case IssueFix.FixRotations:
                QuaternionContinuity.AlignFrames(clip.Frames);
                clip.Revision++;
                break;
            case IssueFix.CleanFootSliding:
                ClipCleanup.CleanFootSliding(clip, rig);
                break;
            case IssueFix.GroundFeet:
                ClipCleanup.GroundFeet(clip, rig);
                break;
            case IssueFix.MakeSeamlessLoop:
                ClipOps.MakeSeamlessLoop(clip, rig, Math.Clamp((int)MathF.Round(clip.Fps * 0.25f), 2, Math.Max(2, clip.FrameCount / 3)));
                break;
            case IssueFix.RemoveRootDrift:
                ClipOps.RemoveRootDrift(clip, rig);
                break;
        }
    }
}
