using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using TextToAnimation.Animation;
using TextToAnimation.Maths;
using TextToAnimation.Vmdl;
using Xunit;

namespace TextToAnimation.Tests;

/// <summary>Saving animations of non-humanoid rigs (with any bone names) into a vmdl: the DMX and the plan.</summary>
public class CreatureSaveTests
{
    const string CreatureVmdl = """
<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->
{
	rootNode =
	{
		_class = "RootNode"
		children =
		[
			{
				_class = "ModelModifierList"
				children =
				[
					{
						_class = "ModelModifier_ScaleAndMirror"
						scale = 0.3937
						mirror_x = false
						mirror_y = false
						mirror_z = false
					},
				]
			},
			{
				_class = "AnimationList"
				children = [ ]
			},
		]
		model_archetype = ""
		base_model_name = ""
	}
}
""";

    /// <summary>Just enough of a keyvalues2 DMX reader for the writer's output: joints, bind pose and channels.</summary>
    sealed class Dmx
    {
        public Dictionary<string, string> ParentOf = new();
        public Dictionary<string, XForm> Bind = new();
        public Dictionary<string, List<Vector3>> Positions = new();
        public Dictionary<string, List<Quaternion>> Orientations = new();

        static float[] Floats(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();

        public static Dmx Parse(string text)
        {
            var d = new Dmx();
            var idToName = new Dictionary<string, string>();
            var childIds = new Dictionary<string, List<string>>();
            foreach (Match m in Regex.Matches(text, "\"DmeJoint\"\\s*\\{\\s*\"id\" \"elementid\" \"(?<id>[^\"]+)\"\\s*\"name\" \"string\" \"(?<name>[^\"]*)\".*?\"children\" \"element_array\"\\s*\\[(?<kids>[^\\]]*)\\]", RegexOptions.Singleline))
            {
                idToName[m.Groups["id"].Value] = m.Groups["name"].Value;
                childIds[m.Groups["name"].Value] = Regex.Matches(m.Groups["kids"].Value, "\"element\" \"([^\"]+)\"").Select(k => k.Groups[1].Value).ToList();
            }
            foreach (var (parent, kids) in childIds)
                foreach (var k in kids) d.ParentOf[idToName[k]] = parent;
            var bind = Regex.Match(text, "\"name\" \"string\" \"bind\"\\s*\"transforms\" \"element_array\"\\s*\\[(?<t>.*?)\\]", RegexOptions.Singleline).Groups["t"].Value;
            foreach (Match m in Regex.Matches(bind, "\"name\" \"string\" \"(?<n>[^\"]*)\"\\s*\"position\" \"vector3\" \"(?<p>[^\"]*)\"\\s*\"orientation\" \"quaternion\" \"(?<o>[^\"]*)\""))
            {
                var p = Floats(m.Groups["p"].Value); var o = Floats(m.Groups["o"].Value);
                d.Bind[m.Groups["n"].Value] = new XForm(new Vector3(p[0], p[1], p[2]), new Quaternion(o[0], o[1], o[2], o[3]));
            }
            foreach (Match m in Regex.Matches(text, "\"DmeChannel\"\\s*\\{\\s*\"name\" \"string\" \"(?<n>[^\"]+)_(?<k>[po])\".*?\"values\" \"(?:vector3|quaternion)_array\"\\s*\\[(?<v>[^\\]]*)\\]", RegexOptions.Singleline))
            {
                var values = Regex.Matches(m.Groups["v"].Value, "\"([^\"]*)\"").Select(x => Floats(x.Groups[1].Value)).ToList();
                if (m.Groups["k"].Value == "p") d.Positions[m.Groups["n"].Value] = values.Select(v => new Vector3(v[0], v[1], v[2])).ToList();
                else d.Orientations[m.Groups["n"].Value] = values.Select(v => new Quaternion(v[0], v[1], v[2], v[3])).ToList();
            }
            return d;
        }
    }

    static MotionRig RenamedRig(string creature, out Func<string, string> rename)
    {
        var spec = RigAnalysisTests.SpecOf(creature);
        rename = Creatures.Gibberish(spec.Select(x => x.Name));
        return MotionRig.Create(Creatures.Build(spec, rename, 3));
    }

    [Theory]
    [InlineData("dog")]
    [InlineData("snake")]
    [InlineData("bird")]
    public void DmxCarriesEveryBoneOfACreatureExactly(string creature)
    {
        var rig = RenamedRig(creature, out _);
        var clip = AnyArmatureTests.Walk(rig, frames: 31);
        const float scale = 0.3937f;
        var dmx = Dmx.Parse(ClipVmdlWriter.BuildDmx(rig, clip.Frames, clip.Fps, false, "walk", scale));
        var s = rig.Skeleton;
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, ClipVmdlWriter.RootYawCompensationDegrees * MathF.PI / 180f);
        XForm Expected(XForm x, bool root)
        {
            var p = x.Pos / scale; var r = x.Rot;
            if (root) { p = Vector3.Transform(p, yaw); r = Quaternion.Normalize(yaw * r); }
            return new XForm(p, r);
        }
        static void Same(XForm want, Vector3 pos, Quaternion rot, string what)
        {
            Assert.True(Vector3.Distance(want.Pos, pos) < 1e-3f * MathF.Max(1f, want.Pos.Length()), $"{what}: position {pos} != {want.Pos}");
            Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(want.Rot, rot)) - 1f) < 1e-5f, $"{what}: orientation {rot} != {want.Rot}");
        }

        Assert.Equal(s.Bones.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal), dmx.Bind.Keys.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var bone in s.Bones)
        {
            var root = bone.ParentIndex < 0;
            Assert.Equal(root ? null : s[bone.ParentIndex].Name, dmx.ParentOf.GetValueOrDefault(bone.Name));
            var bind = Expected(bone.RestLocal, root);
            Same(bind, dmx.Bind[bone.Name].Pos, dmx.Bind[bone.Name].Rot, bone.Name + " bind");
            Assert.Equal(clip.FrameCount, dmx.Positions[bone.Name].Count);
            for (var f = 0; f < clip.FrameCount; f++)
                Same(Expected(clip.Frames[f][bone.Index], root), dmx.Positions[bone.Name][f], dmx.Orientations[bone.Name][f], $"{bone.Name} frame {f}");
        }
    }

    [Fact]
    public void PlansACreatureSaveWithVariantsOrClearNotes()
    {
        var dog = RenamedRig("dog", out _);
        var clip = AnyArmatureTests.Walk(dog);
        ClipCleanup.GenerateFootsteps(clip, dog); // must not throw on a four-legged rig
        clip.Events.Add(new ClipEvent { EventClass = Processing.FootstepEvents.FootstepEventClass, Frame = 12, Foot = "left", Automatic = true });
        clip.Export.AdditiveVariant = true;
        clip.Export.MirroredVariant = true;
        clip.Export.RootMotion = ClipRootMotion.Extract;
        var plan = ClipVmdlWriter.Plan(CreatureVmdl, "models/dog/dog.vmdl", dog,
            new[] { new ClipSaveRequest { Clip = clip, Frames = clip.Frames, SequenceName = "dog_walk" } });
        Assert.Equal(new[] { "dog_walk", "dog_walk_delta", "dog_walk_mirror" }, plan.Sequences);
        Assert.Equal(2, plan.Files.Count);
        var anims = ClipVmdlWriter.ExistingAnimFiles((KvObject)((KvObject)Kv3.Parse(plan.VmdlText).Root)["rootNode"]);
        var extract = ((KvArray)anims["dog_walk"]["children"]).Items.OfType<KvObject>().Single(c => c.GetString("_class") == "ExtractMotion");
        Assert.Equal(dog.Skeleton[dog.RootIndex].Name, extract.GetString("root_bone_name"));
        Assert.Contains(((KvArray)anims["dog_walk"]["children"]).Items.OfType<KvObject>(), c => c.GetString("event_class") == Processing.FootstepEvents.FootstepEventClass);

        var snake = RenamedRig("snake", out _);
        var slither = AnyArmatureTests.Walk(snake);
        slither.Export.MirroredVariant = true;
        var snakePlan = ClipVmdlWriter.Plan(CreatureVmdl, "models/snake/snake.vmdl", snake,
            new[] { new ClipSaveRequest { Clip = slither, Frames = slither.Frames, SequenceName = "slither" } });
        Assert.Equal(new[] { "slither" }, snakePlan.Sequences);
        Assert.Contains(snakePlan.Notes, n => n.Contains("No mirrored copy") && n.Contains("left and right"));
    }
}
