using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Workspace;
using TextToAnimation.Maths;
using TextToAnimation.Rig;
using TextToAnimation.Workspace;
using Xunit;

namespace TextToAnimation.Tests;

public class WorkspaceStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "t2a-tests-" + Guid.NewGuid().ToString("N"));
    readonly MotionRig _rig = Fixtures.HumanRig();

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void RoundTripsClipsAndIdentity()
    {
        var store = new WorkspaceStore(_dir);
        var ws = new AnimationWorkspace { ModelPath = @"Models\Hero\Hero.vmdl", ModelName = "hero", SkeletonFingerprint = AnimationWorkspace.Fingerprint(_rig.Skeleton) };
        var clip = Fixtures.Walk(_rig);
        clip.Name = "Walk";
        clip.PinnedFrames.Add(3);
        clip.LockedBones.Add("head");
        clip.Keys.SetKey("head", 10, new XForm(new System.Numerics.Vector3(1, 2, 3), System.Numerics.Quaternion.Identity));
        clip.Events.Add(new ClipEvent { EventClass = "AE_FOOTSTEP", Frame = 7, Foot = "0", Automatic = true });
        clip.Generation = new GenerationRecord { Mode = "TextToMotion", Prompts = { "walk forward" }, Seed = 42 };
        ws.Clips.Add(clip);
        ws.ActiveClipId = clip.Id;
        store.Save(ws, _rig.Skeleton);

        // a second, similarly named model elsewhere must get its own workspace
        var other = new AnimationWorkspace { ModelPath = "models/other/hero.vmdl" };
        store.Save(other, _rig.Skeleton);

        Assert.Equal(ws.Id, store.FindWorkspaceFor("models/hero/HERO.vmdl"));
        Assert.Equal(other.Id, store.FindWorkspaceFor("models/other/hero.vmdl"));

        var warnings = new List<string>();
        var loaded = store.Load(ws.Id, _rig.Skeleton, warnings);
        Assert.Empty(warnings);
        var c = loaded.Clips.Single();
        Assert.Equal("Walk", c.Name);
        Assert.Equal(clip.FrameCount, c.FrameCount);
        Assert.Equal(clip.Frames[20][_rig.HipsIndex], c.Frames[20][_rig.HipsIndex]);
        Assert.Contains(3, c.PinnedFrames);
        Assert.Contains("head", c.LockedBones);
        Assert.True(c.Keys.HasKey("head", 10));
        Assert.Equal(42, c.Generation.Seed);
        Assert.Equal("AE_FOOTSTEP", c.Events.Single().EventClass);
        Assert.Equal(clip.Id, loaded.ActiveClipId);
    }

    [Fact]
    public void RemapsFramesWhenTheSkeletonChanged()
    {
        var clip = Fixtures.Walk(_rig);
        var data = WorkspaceStore.SerializeFrames(clip, _rig.Skeleton);
        // drop a bone from the "new" model skeleton and add one
        var defs = _rig.Skeleton.Bones.Where(b => b.Name != "hold_L")
            .Select(b => new BoneDefinition(b.Name, b.ParentIndex >= 0 ? _rig.Skeleton[b.ParentIndex].Name : null, b.RestLocal)).ToList();
        defs.Add(new BoneDefinition("new_bone", "pelvis", XForm.Identity));
        var changed = Skeleton.Create(defs);
        var frames = WorkspaceStore.DeserializeFrames(data, changed, out var missing);
        Assert.Equal(1, missing);
        Assert.Equal(clip.Frames[5][_rig.Skeleton.IndexOf("head")], frames[5][changed.IndexOf("head")]);
        Assert.Equal(XForm.Identity, frames[5][changed.IndexOf("new_bone")]);
    }

    [Fact]
    public void RejectsCorruptClipFiles()
    {
        Assert.Throws<InvalidDataException>(() => WorkspaceStore.DeserializeFrames(new byte[] { 1, 2, 3, 4, 5 }, _rig.Skeleton, out _));
        var clip = Fixtures.Walk(_rig);
        var data = WorkspaceStore.SerializeFrames(clip, _rig.Skeleton);
        Assert.Throws<EndOfStreamException>(() => WorkspaceStore.DeserializeFrames(data.Take(data.Length / 2).ToArray(), _rig.Skeleton, out _));
    }
}
