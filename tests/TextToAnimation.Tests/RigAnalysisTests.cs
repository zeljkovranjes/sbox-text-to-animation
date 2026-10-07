using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using TextToAnimation.Core.Rig;
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

    public static TheoryData<string> CreatureNames => new() { "bird", "dog", "snake", "dinosaur", "alligator", "dragon" };

    internal static List<Creatures.Spec> SpecOf(string name) => name switch
    {
        "bird" => Creatures.Bird(),
        "dog" => Creatures.Dog(),
        "snake" => Creatures.Snake(),
        "dinosaur" => Creatures.Dinosaur(),
        "alligator" => Creatures.Alligator(),
        "dragon" => Creatures.Dragon(),
        _ => throw new ArgumentException(name),
    };

    /// <summary>The s&amp;box human with every bone renamed to gibberish and the bone order shuffled.</summary>
    internal static (Skeleton Skeleton, Func<string, string> Rename) RenamedHuman(int seed = 99)
    {
        var human = Fixtures.HumanEngine();
        var rename = Creatures.Gibberish(human.Bones.Select(b => b.Name));
        var defs = human.Bones.Select(b => new BoneDefinition(rename(b.Name), b.ParentIndex < 0 ? null : rename(human[b.ParentIndex].Name), b.RestLocal)).ToList();
        var rng = new Random(seed);
        return (Skeleton.Create(defs.OrderBy(_ => rng.Next()).ToList()), rename);
    }

    /// <summary>Asserts two analyses of the same rig (one renamed/reordered) agree bone for bone.</summary>
    static void AssertSameAnatomy(RigAnalysis a, RigAnalysis b, Func<string, string> rename, bool namesUsed)
    {
        var sa = a.Skeleton; var sb = b.Skeleton;
        int Map(int i) => i < 0 ? -1 : sb.IndexOf(rename(sa[i].Name));
        Assert.True(Vector3.Distance(a.Forward, b.Forward) < 1e-4f, $"forward {a.Forward} vs {b.Forward}");
        Assert.Equal(Map(a.BodyRoot), b.BodyRoot);
        Assert.Equal(Map(a.Head), b.Head);
        Assert.Equal(a.Facing, b.Facing);
        Assert.Equal(Map(a.FacingRight), b.FacingRight);
        Assert.Equal(Map(a.FacingLeft), b.FacingLeft);
        Assert.Equal(a.IsHumanoid, b.IsHumanoid);
        for (var i = 0; i < sa.Count; i++)
        {
            var j = Map(i);
            Assert.True(a.InBody[i] == b.InBody[j], $"{sa[i].Name}: in body {a.InBody[i]} vs {b.InBody[j]}");
            Assert.Equal(a.Side[i], b.Side[j]);
            Assert.Equal(a.Part[i], b.Part[j]);
            Assert.Equal(Map(a.Mirror[i]), b.Mirror[j]);
            if (a.Part[i] != RigPart.Other || !namesUsed) Assert.True(a.Label[i] == b.Label[j], $"{sa[i].Name}: '{a.Label[i]}' vs '{b.Label[j]}'");
        }
    }

    [Theory]
    [MemberData(nameof(CreatureNames))]
    public void CreatureAnatomyDoesNotDependOnNamesOrBoneOrder(string creature)
    {
        var spec = SpecOf(creature);
        var named = RigAnalysis.Analyze(Creatures.Build(spec));
        var rename = Creatures.Gibberish(spec.Select(x => x.Name));
        var renamed = RigAnalysis.Analyze(Creatures.Build(spec, rename, shuffleSeed: 77));
        _out.WriteLine(Dump(named));
        AssertSameAnatomy(named, renamed, rename, namesUsed: true);
        // every body bone is explained by the shape
        Assert.DoesNotContain(Enumerable.Range(0, named.Skeleton.Count), b => named.InBody[b] && named.Part[b] == RigPart.Other);
    }

    [Fact]
    public void HumanAnatomyDoesNotDependOnNamesOrBoneOrder()
    {
        var named = RigAnalysis.Analyze(Fixtures.HumanEngine());
        var (skeleton, rename) = RenamedHuman();
        var renamed = RigAnalysis.Analyze(skeleton);
        AssertSameAnatomy(named, renamed, rename, namesUsed: false);
        Assert.True(renamed.IsHumanoid);
    }

    [Fact]
    public void ReadsABird()
    {
        var a = RigAnalysis.Analyze(Creatures.Build(Creatures.Bird()));
        var s = a.Skeleton;
        string L(string bone) => a.Label[s.IndexOf(bone)];
        Assert.False(a.IsHumanoid);
        Assert.Equal("pelvis", s[a.BodyRoot].Name);
        Assert.Equal("head", s[a.Head].Name);
        Assert.True(Vector3.Dot(a.Forward, Vector3.UnitX) > 0.99f);
        Assert.Equal("Left Thigh", L("thigh_L"));
        Assert.Equal("Right Foot", L("tarsus_R"));
        Assert.Equal("Left Toe", L("toe_a_L"));
        Assert.Equal("Right Wing", L("wing_3_R"));
        Assert.Equal("Tail", L("tail_2"));
        Assert.Equal("Neck", L("neck_2"));
        Assert.Equal("Jaw", L("beak"));
        Assert.Equal(("thigh_R", "thigh_L"), (s[a.FacingRight].Name, s[a.FacingLeft].Name));
    }

    [Fact]
    public void ReadsADog()
    {
        var a = RigAnalysis.Analyze(Creatures.Build(Creatures.Dog()));
        var s = a.Skeleton;
        string L(string bone) => a.Label[s.IndexOf(bone)];
        Assert.False(a.IsHumanoid);
        Assert.Equal("pelvis", s[a.BodyRoot].Name);
        Assert.Equal("head", s[a.Head].Name);
        Assert.Equal("Left Thigh", L("hip_L"));
        Assert.Equal("Right Shin", L("knee_R"));
        Assert.Equal("Left Fetlock", L("hock_L"));
        Assert.Equal("Right Foot", L("paw_R"));
        Assert.Equal("Left Upper Arm", L("scapula_L"));
        Assert.Equal("Left Forearm", L("elbow_L"));
        Assert.Equal("Right Finger", L("ftoe_R"));
        Assert.Equal("Tail", L("tail_3"));
        Assert.Equal("Left Ear", L("ear_L"));
        Assert.Equal(4, a.Limbs.Count(l => l.Kind is LimbKind.Leg or LimbKind.FrontLeg));
        Assert.Equal(("hip_R", "hip_L"), (s[a.FacingRight].Name, s[a.FacingLeft].Name));
    }

    [Fact]
    public void ReadsASnakeAlongItsBody()
    {
        var a = RigAnalysis.Analyze(Creatures.Build(Creatures.Snake()));
        var s = a.Skeleton;
        Assert.False(a.Symmetric);
        Assert.Equal(RigFacing.BodyAxis, a.Facing);
        Assert.True(Vector3.Dot(a.Forward, Vector3.UnitX) > 0.99f, $"forward {a.Forward}");
        Assert.Equal("jaw", s[a.FacingRight].Name);
        Assert.Equal("root", s[a.FacingLeft].Name);
        Assert.Equal("Spine", a.Label[s.IndexOf("seg_10")]);
    }

    [Fact]
    public void ReadsADinosaurAndAnAlligator()
    {
        var dino = RigAnalysis.Analyze(Creatures.Build(Creatures.Dinosaur()));
        var ds = dino.Skeleton;
        Assert.False(dino.IsHumanoid);
        Assert.Equal("Left Upper Arm", dino.Label[ds.IndexOf("arm_L")]);
        Assert.Equal("Right Hand", dino.Label[ds.IndexOf("hand_R")]);
        Assert.Equal("Tail", dino.Label[ds.IndexOf("tail_4")]);
        Assert.Equal("Left Toe", dino.Label[ds.IndexOf("toe_L")]);

        var gator = RigAnalysis.Analyze(Creatures.Build(Creatures.Alligator()));
        var gs = gator.Skeleton;
        Assert.Equal("Left Thigh", gator.Label[gs.IndexOf("hip_L")]);
        Assert.Equal("Right Upper Arm", gator.Label[gs.IndexOf("shoulder_R")]);
        Assert.Equal("Tail", gator.Label[gs.IndexOf("tail_5")]);
        Assert.Equal("head", gs[gator.Head].Name);
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
