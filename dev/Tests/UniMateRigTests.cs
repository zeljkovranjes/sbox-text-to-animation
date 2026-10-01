using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Generation;
using Xunit;

namespace TextToAnimation.Tests;

public class UniMateRigTests
{
    sealed class Golden
    {
        public string[] bones { get; set; }
        public string[] names { get; set; }
        public int[] parents { get; set; }
        public float[][] tpose { get; set; }
        public float[][] restPos { get; set; }
        public int rh { get; set; }
        public int lh { get; set; }
        public float scale { get; set; }
    }

    /// <summary>The shape-based joint selection reproduces the old role-table rig for the s&amp;box human bit for bit.</summary>
    [Fact]
    public void SboxHumanMatchesTheRoleTableRigExactly()
    {
        var golden = JsonSerializer.Deserialize<Golden>(File.ReadAllText(Fixtures.Path("unimate/golden_human_unimate_rig.json")))!;
        var rig = Fixtures.HumanRig();
        var u = UniMateRig.Build(rig);
        var s = u.Skeleton;
        Assert.Equal(RigFamily.Humanoid, u.Family);
        Assert.Equal(golden.bones, u.Bone.Select(b => b < 0 ? null : rig.Skeleton[b].Name).ToArray());
        Assert.Equal(golden.names, s.CleanNames);
        Assert.Equal(golden.parents, s.Parents);
        Assert.Equal(golden.rh, s.RightHip);
        Assert.Equal(golden.lh, s.LeftHip);
        Assert.Equal(golden.scale, s.Scale);
        for (var j = 0; j < s.Count; j++)
        {
            Assert.Equal(golden.tpose[j], new[] { s.TPose[j].X, s.TPose[j].Y, s.TPose[j].Z });
            Assert.Equal(golden.restPos[j], new[] { s.RestWorldPos[j].X, s.RestWorldPos[j].Y, s.RestWorldPos[j].Z });
        }
    }
}
