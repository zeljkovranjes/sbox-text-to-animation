using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using TextToAnimation.Animation;
using TextToAnimation.Maths;
using TextToAnimation.Rig;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// The real s&amp;box human skeleton exactly as the engine reports it (dumped by the in-editor gate: 96 bones with
/// IK helpers, twist bones, face bones and the engine's rest pose), not the reduced fixture rig.
/// </summary>
public class EngineSkeletonTests
{
    readonly ITestOutputHelper _out;
    public EngineSkeletonTests(ITestOutputHelper output) => _out = output;

    public static Skeleton LoadEngineSkeleton(string file = "citizen_human_engine_skeleton.json")
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file)));
        var bones = doc.RootElement.GetProperty("bones").EnumerateArray().ToList();
        var names = bones.Select(b => b.GetProperty("name").GetString()!).ToList();
        var defs = bones.Select(b =>
        {
            var p = b.GetProperty("pos").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var r = b.GetProperty("rot").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var parent = b.GetProperty("parent").GetInt32();
            return new BoneDefinition(b.GetProperty("name").GetString()!, parent < 0 ? null : names[parent],
                new XForm(new Vector3(p[0], p[1], p[2]), new Quaternion(r[0], r[1], r[2], r[3])));
        }).ToList();
        return Skeleton.Create(defs);
    }

    [Fact]
    public void RealSboxHumanIsAHumanoid()
    {
        var skeleton = LoadEngineSkeleton();
        var a = RigAnalysis.Analyze(skeleton);
        foreach (var l in a.Limbs) _out.WriteLine($"{l.Kind} {l.Side}: {string.Join(",", l.Chain.Select(b => skeleton[b].Name))}");
        _out.WriteLine("spine: " + string.Join(",", a.SpineChain.Select(b => skeleton[b].Name)));
        foreach (var n in new[] { "clavicle_L", "clavicle_R", "eye_L", "eye_R", "arm_upper_L", "arm_upper_R" })
        {
            var i = skeleton.IndexOf(n);
            _out.WriteLine($"{n}: side {a.Side[i]} mirror {skeleton[a.Mirror[i]].Name} label {a.Label[i]}");
        }
        Assert.True(a.IsHumanoid);
        Assert.DoesNotContain(a.SpineChain, b => skeleton[b].Name.StartsWith("clavicle", StringComparison.Ordinal));
        Assert.Equal(2, a.Limbs.Count(l => l.Kind == LimbKind.Arm));
        var rig = MotionRig.Create(skeleton);
        Assert.True(rig.IsHumanoid, string.Join("; ", rig.Problems));
        Assert.Equal("pelvis", skeleton[rig.HipsIndex].Name);
    }
}
