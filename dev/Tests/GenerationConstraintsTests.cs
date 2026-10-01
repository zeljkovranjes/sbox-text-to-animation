using System;
using System.Linq;
using System.Numerics;
using TextToAnimation.Animation;
using TextToAnimation.Maths;
using Xunit;

namespace TextToAnimation.Tests;

public class GenerationConstraintsTests
{
    readonly MotionRig _rig = Fixtures.HumanRig();

    /// <summary>A "generated" copy of the walk with every bone perturbed (as model drift would).</summary>
    static System.Collections.Generic.List<XForm[]> Perturbed(System.Collections.Generic.List<XForm[]> frames)
    {
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f);
        return frames.Select(pose => pose.Select(x => new XForm(x.Pos + new Vector3(1f, 0, 0), MathQ.Normalize(tilt * x.Rot))).ToArray()).ToList();
    }

    [Fact]
    public void PinnedFramesAreRestoredExactlyAndBlendSmoothly()
    {
        var source = Fixtures.Walk(_rig).Frames;
        var generated = Perturbed(source);
        GenerationConstraints.RestorePins(generated, source, new[] { 0, 30, 59 }, blendFrames: 6);

        foreach (var p in new[] { 0, 30, 59 })
            Assert.Equal(source[p], generated[p]);

        // the correction fades out: 1 frame away is closer to the source than 5 frames away, and far frames are untouched
        var hips = _rig.HipsIndex;
        float Err(int f) => MathQ.AngleBetween(source[f][hips].Rot, generated[f][hips].Rot);
        Assert.True(Err(31) < Err(35), $"{Err(31)} vs {Err(35)}");
        var untouched = Perturbed(source)[45];
        Assert.Equal(untouched, generated[45]);

        // no pop between consecutive frames around a pin
        for (var f = 24; f < 37; f++)
            Assert.True(MathQ.AngleBetween(generated[f][hips].Rot, generated[f + 1][hips].Rot) < 0.2f / 3f, $"jump at {f}");
    }

    [Fact]
    public void PinBlendNeverCrossesANeighbouringPin()
    {
        var source = Fixtures.Walk(_rig).Frames;
        var generated = Perturbed(source);
        GenerationConstraints.RestorePins(generated, source, new[] { 10, 12 }, blendFrames: 6);
        Assert.Equal(source[10], generated[10]);
        Assert.Equal(source[12], generated[12]);
    }

    [Fact]
    public void LockedBonesKeepTheirSourceMotionOnEveryFrame()
    {
        var source = Fixtures.Walk(_rig).Frames;
        var generated = Perturbed(source);
        var locked = new[] { "leg_upper_L", "leg_lower_L", "ankle_L" }.Select(_rig.Skeleton.IndexOf).ToArray();
        Assert.DoesNotContain(-1, locked);
        GenerationConstraints.RestoreBones(generated, source, locked);
        var free = _rig.Skeleton.IndexOf("arm_upper_R");
        for (var f = 0; f < source.Count; f++)
        {
            foreach (var b in locked) Assert.Equal(source[f][b], generated[f][b]);
            Assert.NotEqual(source[f][free], generated[f][free]);
        }
    }
}
