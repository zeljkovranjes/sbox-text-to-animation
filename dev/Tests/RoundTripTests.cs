using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Maths;
using TextToAnimation.Processing;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// Pose -> UniMate joints -> features -> decode -> workspace bones must give the pose back, on the real engine
/// skeletons (the s&amp;box human and FBX creatures as the engine reports them). Every animated bone's local
/// rotation is compared, so a wrong twist (which joint positions alone would hide) fails here.
/// </summary>
public class RoundTripTests
{
    readonly ITestOutputHelper _out;
    public RoundTripTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("citizen_human_engine_skeleton.json")]
    [InlineData("engine_skeleton_fox.json")]
    [InlineData("engine_skeleton_brainstem.json")]
    [InlineData("engine_skeleton_shark.json")]
    [InlineData("engine_skeleton_octopus.json")]
    public void EncodeDecodeReturnsThePose(string file)
    {
        var skeleton = EngineSkeletonTests.LoadEngineSkeleton(file);
        var rig = MotionRig.Create(skeleton);
        var uni = UniMateRig.Build(rig);

        // a pose: every bone bent a little about its own axes (deterministic), root moved and turned
        var frames = new List<XForm[]>();
        for (var t = 0; t < 4; t++)
        {
            var pose = skeleton.Bones.Select(b => b.RestLocal).ToArray();
            for (var b = 0; b < pose.Length; b++)
            {
                var axis = Vector3.Normalize(new Vector3(MathF.Sin(b * 1.7f + t), MathF.Cos(b * 0.9f), MathF.Sin(b * 2.3f + 0.5f)));
                pose[b].Rot = MathQ.Normalize(pose[b].Rot * Quaternion.CreateFromAxisAngle(axis, 0.25f + 0.05f * t));
            }
            frames.Add(pose);
        }

        var (pos, rot) = uni.JointWorld(frames);
        var (feat, align) = UniMateFeatures.Encode(pos, rot, uni.Skeleton);
        var motion = UniMateFeatures.Decode(feat, uni.Skeleton.Parents);
        UniMateFeatures.Unalign(motion, align);
        var src = UniMateFeatures.ToSource(motion, uni.Skeleton);
        var back = uni.ToFrames(src, frames);

        var worst = 0f; var worstBone = "";
        // upstream's representation stores a joint's rotation in its children's slots (HML order), so a leaf joint's
        // own rotation isn't carried (upstream reconstructs leaves following their parent): compare the others
        var hasChild = Enumerable.Range(0, uni.Count).Select(j => uni.Skeleton.Parents.Contains(j)).ToArray();
        var animated = Enumerable.Range(0, uni.Count).Where(j => hasChild[j]).Select(j => uni.Bone[j]).ToHashSet();
        var wa = new XForm[skeleton.Count]; var wb = new XForm[skeleton.Count];
        for (var t = 0; t < frames.Count - 1; t++) // the last frame has no velocity
        {
            FkUtil.ToWorld(frames[t], skeleton, wa);
            FkUtil.ToWorld(back[t], skeleton, wb);
            foreach (var b in animated)
            {
                var e = MathQ.AngleBetween(wa[b].Rot, wb[b].Rot) * 180f / MathF.PI;
                if (e > worst) { worst = e; worstBone = skeleton[b].Name; }
            }
        }
        _out.WriteLine($"{file}: {uni.Count} joints, worst world rotation error {worst:0.000} deg at {worstBone}");
        Assert.True(worst < 0.5f, $"{file}: {worstBone} comes back {worst:0.0} deg off");
    }
}
