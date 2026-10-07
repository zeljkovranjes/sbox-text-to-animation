using System;
using System.Linq;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Inference.UniMate;
using Xunit;

namespace TextToAnimation.Tests;

public class UniMateRigTests
{
    /// <summary>
    /// The s&amp;box human under the people statistics is shown to UniMate as the Mixamo bodies it learned people
    /// from: exactly their 22 joints with their names, arms in a T-pose; fingers, twists, eyes and hold points follow.
    /// </summary>
    [Fact]
    public void SboxHumanIsShownAsAMixamoBody()
    {
        var rig = Fixtures.HumanRig();
        var u = UniMateRig.Build(rig);
        Assert.True(u.AsMixamoBody);
        Assert.Equal(22, u.Count);
        var mixamo = new[] { "Hips", "Spine", "Right Thigh", "Left Thigh", "Spine", "Right Shin", "Left Shin", "Spine", "Right Foot", "Left Foot",
            "Left Shoulder", "Right Shoulder", "Neck", "Right Toe", "Left Toe", "Left Upper Arm", "Right Upper Arm", "Head", "Left Forearm",
            "Right Forearm", "Left Hand", "Right Hand" };
        Assert.Equal(mixamo.OrderBy(n => n), u.Skeleton.CleanNames.OrderBy(n => n));
        // the arms UniMate sees stand level, straight out to the sides
        var t = u.Skeleton.TPose;
        int J(string n) => Enumerable.Range(0, u.Count).First(j => u.Skeleton.CleanNames[j] == n);
        foreach (var side in new[] { "Left", "Right" })
        {
            var d = System.Numerics.Vector3.Normalize(t[J(side + " Hand")] - t[J(side + " Upper Arm")]);
            Assert.True(MathF.Abs(d.Y) < 0.1f, $"{side} arm not level: {d}");
        }
        // upstream's own preparation (what the comparison tests use) keeps every body bone and the rig's rest pose
        var up = UniMateRig.Build(rig, alignVocabulary: false, skipHelpers: false, peopleBody: false);
        Assert.False(up.AsMixamoBody);
        Assert.True(up.Count > 22);
    }

    /// <summary>With skin weights attached, unskinned helper bones are pruned as upstream prunes them.</summary>
    [Fact]
    public void UnskinnedHelpersArePrunedWithSkinData()
    {
        var rig = Fixtures.HumanRig();
        var s = rig.Skeleton;
        // skin everything except leaves named like helpers (twist/IK end bones get no weights in practice)
        var weights = Enumerable.Range(0, s.Count).ToDictionary(b => s[b].Name,
            b => s.Bones.Any(c => c.ParentIndex == b) || !s[b].Name.Contains("twist") ? (1.0, 10.0) : (0.0, 0.0));
        UniMateSkin.Attach(s, weights, "citizen");
        try
        {
            var u = UniMateRig.Build(rig);
            Assert.DoesNotContain(u.Bone, b => weights[s[b].Name].Item1 == 0);
        }
        finally { UniMateSkin.Attach(s, null, ""); }
    }
}
