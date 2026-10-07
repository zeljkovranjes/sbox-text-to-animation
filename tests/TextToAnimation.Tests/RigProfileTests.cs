using System;
using System.Collections.Generic;
using System.Linq;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Inference.UniMate;
using TextToAnimation.EditorTools.Mapping;
using TextToAnimation.Core.Rig;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// Person rigs of every convention humanoid-retargeter knows (its 20 profiles) reach UniMate as the same Mixamo body:
/// the s&amp;box human renamed bone by bone into each convention must be prepared exactly like the s&amp;box human.
/// </summary>
public class RigProfileTests
{
    readonly ITestOutputHelper _out;
    public RigProfileTests(ITestOutputHelper o) => _out = o;

    public static IEnumerable<object[]> Profiles() => ProfileLibrary.All.Select(p => new object[] { p.Name });

    /// <summary>The s&amp;box human with every bone the s&amp;box profile maps renamed to <paramref name="profile"/>'s first alias for its role.</summary>
    static MotionRig Renamed(Profile profile)
    {
        var human = Fixtures.HumanEngine();
        var sbox = ProfileDetector.Detect(human) ?? throw new InvalidOperationException("the s&box human isn't recognised");
        var rename = new Dictionary<int, string>();
        foreach (var (role, bone) in sbox.Result.RoleToBone)
            if (profile.Aliases.TryGetValue(role, out var aliases) && aliases.Length > 0) rename[bone] = aliases[0];
        string Name(int b) => rename.TryGetValue(b, out var n) ? n : human[b].Name;
        // names a profile uses for its roles must not collide with bones left as they were
        var used = rename.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Unique(int b) => rename.ContainsKey(b) || !used.Contains(human[b].Name) ? Name(b) : human[b].Name + "_kept";
        var defs = human.Bones.Select(b => new BoneDefinition(Unique(b.Index), b.ParentIndex < 0 ? null : Unique(b.ParentIndex), b.RestLocal)).ToList();
        return MotionRig.Create(Skeleton.Create(defs));
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryConventionReachesUniMateAsTheSameBody(string profileName)
    {
        var profile = ProfileLibrary.All.First(p => p.Name == profileName);
        var reference = UniMateRig.Build(Fixtures.HumanRig());
        var rig = Renamed(profile);
        var detected = ProfileDetector.Detect(rig.Skeleton);
        var u = UniMateRig.Build(rig);
        _out.WriteLine($"{profileName}: detected {detected?.Profile.Name ?? "none"} ({detected?.Result.Confidence:0.00}); {u.Count} joints, Mixamo body {u.AsMixamoBody}: {string.Join(", ", u.Skeleton.CleanNames)}");
        Assert.NotNull(detected);
        Assert.True(u.AsMixamoBody, $"{profileName}: not prepared as a person");
        // the same joints (the same bones underneath) under the same names
        // (a convention without some optional roles - Perception Neuron has no toes - simply lacks those joints)
        var sbox = ProfileDetector.Detect(Fixtures.HumanEngine())!.Value.Result.RoleToBone.ToDictionary(kv => kv.Value, kv => kv.Key);
        var expected = Enumerable.Range(0, reference.Count).Where(j => !sbox.TryGetValue(reference.Bone[j], out var role) || profile.Aliases.ContainsKey(role)).ToList();
        Assert.Equal(expected.Select(j => reference.Motion.Skeleton.RestWorld[reference.Bone[j]].Pos), u.Bone.Select(b => rig.Skeleton.RestWorld[b].Pos));
        Assert.Equal(expected.Select(j => reference.Skeleton.CleanNames[j]), u.Skeleton.CleanNames);
    }

    /// <summary>Animals rigged with a person's naming convention (3ds Max Biped dogs and bears) stay animals.</summary>
    [Fact]
    public void AnimalsWithPersonBoneNamesStayAnimals()
    {
        foreach (var key in new[] { "Dog", "Bear", "Horse", "Crocodile" })
        {
            var e = NamingAgreementTests.Load().First(x => x.dataset == "truebones" && x.key == key);
            var u = UniMateRig.Build(NamingAgreementTests.RigOf(e));
            _out.WriteLine($"{key}: {u.Family}, Mixamo body {u.AsMixamoBody}, {u.Count} joints");
            Assert.False(u.AsMixamoBody, key);
            Assert.NotEqual(TextToAnimation.Core.Generation.RigFamily.Humanoid, u.Family);
        }
    }
}
