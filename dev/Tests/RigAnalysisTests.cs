using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using TextToAnimation.Rig;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

public class JointNamesTests
{
    [Fact]
    public void MatchesTheReferenceCleaner()
    {
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Fixtures.Path("unimate/clean_names.json")))!;
        var wrong = expected.Where(kv => JointNames.Clean(kv.Key) != kv.Value).Select(kv => $"{kv.Key}: '{JointNames.Clean(kv.Key)}' != '{kv.Value}'").ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

public class RigAnalysisTests
{
    readonly ITestOutputHelper _out;
    public RigAnalysisTests(ITestOutputHelper o) => _out = o;

    internal static string Dump(RigAnalysis a)
    {
        var s = a.Skeleton;
        var lines = new List<string>
        {
            $"forward {a.Forward} ({a.ForwardSource}) left {a.Left} symmetric {a.Symmetric} normal {a.MirrorNormal} humanoid {a.IsHumanoid}",
            $"hips {s[a.BodyRoot].Name} head {(a.Head >= 0 ? s[a.Head].Name : "-")} facing {a.Facing} {(a.FacingRight >= 0 ? s[a.FacingRight].Name : "-")}/{(a.FacingLeft >= 0 ? s[a.FacingLeft].Name : "-")}",
        };
        for (var b = 0; b < s.Count; b++)
            lines.Add($"  {s[b].Name,-24} {(a.InBody[b] ? "body" : "----")} {a.Side[b],-6} {a.Part[b],-9} {a.Label[b],-18} mirror {(a.Mirror[b] != b ? s[a.Mirror[b]].Name : "")}");
        foreach (var l in a.Limbs)
            lines.Add($"  limb {l.Kind} {l.Side} grounded {l.Grounded} chain [{string.Join(", ", l.Chain.Select(c => s[c].Name))}] digits {l.Digits.Count}");
        return string.Join("\n", lines);
    }

    [Fact]
    public void ReadsTheSboxHuman()
    {
        var a = RigAnalysis.Analyze(Fixtures.HumanEngine());
        _out.WriteLine(Dump(a));
        var s = a.Skeleton;
        Assert.True(a.IsHumanoid);
        Assert.Equal("pelvis", s[a.BodyRoot].Name);
        Assert.Equal("head", s[a.Head].Name);
        Assert.True(Vector3.Dot(a.Forward, Vector3.UnitX) > 0.99f, $"forward {a.Forward}");
        Assert.Equal(RigFacing.Pair, a.Facing);
        Assert.Equal("leg_upper_R", s[a.FacingRight].Name);
        Assert.Equal("leg_upper_L", s[a.FacingLeft].Name);
        Assert.Equal("Left Thigh", a.Label[s.IndexOf("leg_upper_L")]);
        Assert.Equal("Right Upper Arm", a.Label[s.IndexOf("arm_upper_R")]);
        Assert.Equal("Left Shoulder", a.Label[s.IndexOf("clavicle_L")]);
        Assert.Equal("Right Toe", a.Label[s.IndexOf("ball_R")]);
        Assert.Equal(s.IndexOf("arm_lower_L"), a.Mirror[s.IndexOf("arm_lower_R")]);
        Assert.False(a.InBody[s.IndexOf("arm_upper_L_twist0")]);
        Assert.False(a.InBody[s.IndexOf("root_IK")]);
    }
}
