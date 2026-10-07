using System;
using System.IO;
using System.Linq;
using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Vmdl;
using Xunit;

namespace TextToAnimation.Tests;

public class ClipVmdlWriterTests
{
    readonly MotionRig _rig = Fixtures.HumanRig();

    const string SmallVmdl = """
<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->
{
	rootNode =
	{
		_class = "RootNode"
		children =
		[
			{
				_class = "AnimationList"
				children =
				[
					{
						_class = "AnimFile"
						name = "idle"
						children =
						[
							{
								_class = "AnimEvent"
								event_class = "AE_CUSTOM"
								event_frame = 3
							},
						]
						activity_name = "ACT_IDLE"
						fade_in_time = 0.5
						looping = true
						source_filename = "anims/idle.fbx"
						take = 2
					},
				]
				default_root_bone_name = "pelvis"
			},
		]
		model_archetype = ""
		base_model_name = ""
	}
}
""";

    [Fact]
    public void AddsNewSequenceWithDmxInOwnedFolder()
    {
        var clip = Fixtures.Walk(_rig);
        clip.Looping = true;
        var plan = ClipVmdlWriter.Plan(SmallVmdl, "models/hero/hero.vmdl", _rig,
            new[] { new ClipSaveRequest { Clip = clip, Frames = clip.Frames, SequenceName = "walk_gen" } });
        Assert.Single(plan.Files);
        Assert.Equal("models/hero/text_to_animation/hero/walk_gen.dmx", plan.Files[0].AssetPath);
        var anims = ClipVmdlWriter.ExistingAnimFiles((KvObject)((KvObject)Kv3.Parse(plan.VmdlText).Root)["rootNode"]);
        Assert.True(anims.ContainsKey("idle"));
        Assert.Equal("models/hero/text_to_animation/hero/walk_gen.dmx", anims["walk_gen"].GetString("source_filename"));
        Assert.Contains("frameRate", plan.Files[0].Content);
        Assert.DoesNotContain("E-0", plan.VmdlText);
    }

    [Fact]
    public void ReplaceKeepsUserSettingsButSwapsData()
    {
        var clip = Fixtures.Walk(_rig);
        clip.Events.Add(new ClipEvent { EventClass = "AE_FOOTSTEP", Frame = 4, Foot = "0", Automatic = true });
        var plan = ClipVmdlWriter.Plan(SmallVmdl, "models/hero/hero.vmdl", _rig,
            new[] { new ClipSaveRequest { Clip = clip, Frames = clip.Frames, SequenceName = "idle", ReplaceExisting = true } });
        var idle = ClipVmdlWriter.ExistingAnimFiles((KvObject)((KvObject)Kv3.Parse(plan.VmdlText).Root)["rootNode"])["idle"];
        Assert.Equal("models/hero/text_to_animation/hero/idle.dmx", idle.GetString("source_filename"));
        Assert.Equal("ACT_IDLE", idle.GetString("activity_name"));
        Assert.Equal(0.5, ((KvDouble)idle["fade_in_time"]).Value);
        Assert.Equal(0L, ((KvLong)idle["take"]).Value);
        var events = ((KvArray)idle["children"]).Items.OfType<KvObject>().Select(o => o.GetString("event_class")).ToList();
        Assert.Contains("AE_CUSTOM", events);   // the user's own event survives
        Assert.Contains("AE_FOOTSTEP", events); // ours is added
        Assert.Equal(SmallVmdl, plan.OriginalVmdlText);
    }

    [Fact]
    public void RefusesSilentOverwriteAndUnknownReplace()
    {
        var clip = Fixtures.Walk(_rig);
        Assert.Throws<InvalidOperationException>(() => ClipVmdlWriter.Plan(SmallVmdl, "m/h.vmdl", _rig,
            new[] { new ClipSaveRequest { Clip = clip, Frames = clip.Frames, SequenceName = "idle" } }));
        Assert.Throws<InvalidOperationException>(() => ClipVmdlWriter.Plan(SmallVmdl, "m/h.vmdl", _rig,
            new[] { new ClipSaveRequest { Clip = clip, Frames = clip.Frames, SequenceName = "run", ReplaceExisting = true } }));
    }

    [Fact]
    public void ScalesPositionsByTheModelScaleAndAddsVariants()
    {
        var text = File.ReadAllText(Fixtures.Path("citizen_human_male.vmdl"));
        var root = (KvObject)((KvObject)Kv3.Parse(text).Root)["rootNode"];
        Assert.Equal(0.3937f, ClipVmdlWriter.ModelScale(root, out var mirrored), 4);
        Assert.False(mirrored);

        var clip = Fixtures.Walk(_rig);
        clip.Export.AdditiveVariant = true;
        clip.Export.MirroredVariant = true;
        clip.Export.RootMotion = ClipRootMotion.Extract;
        var plan = ClipVmdlWriter.Plan(text, "models/citizen_human/citizen_human_male.vmdl", _rig,
            new[] { new ClipSaveRequest { Clip = clip, Frames = clip.Frames, SequenceName = "gen_walk" } });
        Assert.Contains("gen_walk_delta", plan.Sequences);
        Assert.Contains("gen_walk_mirror", plan.Sequences);
        var anims = ClipVmdlWriter.ExistingAnimFiles((KvObject)((KvObject)Kv3.Parse(plan.VmdlText).Root)["rootNode"]);
        var walk = anims["gen_walk"];
        Assert.Contains(((KvArray)walk["children"]).Items.OfType<KvObject>(), c => c.GetString("_class") == "ExtractMotion" && c.GetString("root_bone_name") == "pelvis");
        Assert.Equal("gen_walk", ((KvArray)anims["gen_walk_delta"]["children"]).Items.OfType<KvObject>().Single().GetString("anim_name"));
        // the DMX bind of a child bone is the engine value divided by the model scale (back to cm)
        var head = _rig.Skeleton[_rig.Skeleton.IndexOf("head")].RestLocal.Pos.Length();
        Assert.True(head / 0.3937f > head * 2f);
    }
}
