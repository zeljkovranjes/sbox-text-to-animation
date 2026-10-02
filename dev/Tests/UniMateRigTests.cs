using System.Linq;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Inference.UniMate;
using Xunit;

namespace TextToAnimation.Tests;

public class UniMateRigTests
{
    /// <summary>
    /// The s&amp;box human goes through upstream's preparation like any rig: without skin data nothing is pruned,
    /// every joint is a real bone with upstream's clean name, and the facing is upstream's rule.
    /// </summary>
    [Fact]
    public void SboxHumanGoesThroughUpstreamPreparation()
    {
        var rig = Fixtures.HumanRig();
        var u = UniMateRig.Build(rig);
        // two root trees (pelvis and the IK helpers under root_IK): upstream prune_secondary_roots keeps the larger one
        int RootOf(int b) { while (rig.Skeleton[b].ParentIndex >= 0) b = rig.Skeleton[b].ParentIndex; return b; }
        var main = Enumerable.Range(0, rig.Skeleton.Count).GroupBy(RootOf).OrderByDescending(g => g.Count()).First();
        Assert.All(u.Bone, b => Assert.Equal(main.Key, RootOf(b)));
        // the body is animated; fingers (absent from Mixamo, whose statistics a person gets) and procedural helper
        // bones (twist, helper, IK, clothing) are left to follow
        var names = u.Bone.Select(b => rig.Skeleton[b].Name).ToHashSet();
        Assert.Subset(names, new HashSet<string> { "pelvis", "spine_0", "head", "arm_upper_L", "arm_lower_R", "hand_L", "leg_upper_R", "leg_lower_L", "ankle_R" });
        Assert.DoesNotContain(names, n => n.Contains("finger") || n.Contains("twist") || n.Contains("helper"));
        Assert.All(u.Bone, b => Assert.True(b >= 0));
        for (var j = 0; j < u.Count; j++) Assert.Equal(UniMateVocabulary.Align(UniMateNames.Clean(rig.Skeleton[u.Bone[j]].Name, "")), u.Skeleton.CleanNames[j]);
        Assert.Equal("Hips", u.Skeleton.CleanNames[0]);
        Assert.True(u.Skeleton.RightHip >= 0 && u.Skeleton.LeftHip >= 0);
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
