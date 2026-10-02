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

    [Fact]
    public void PromptHistoryRoundTripsNewestFirstAndIsCapped()
    {
        var store = new WorkspaceStore(_dir);
        var ws = new AnimationWorkspace { ModelPath = "models/hero.vmdl" };
        store.Save(ws, _rig.Skeleton);
        Assert.Null(store.LoadPromptHistory(ws.Id)); // never written: the session seeds it from the clips

        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clipId = Guid.NewGuid();
        var entries = Enumerable.Range(0, WorkspaceStore.MaxPromptHistory + 25)
            .Select(i => new PromptHistoryEntry { Prompt = $"prompt {i}", Mode = "TextToMotion", Seed = i, CreatedUtc = start.AddMinutes(i), ClipIds = { clipId }, ClipName = "Walk" })
            .ToList();
        store.SavePromptHistory(ws.Id, entries);

        var loaded = store.LoadPromptHistory(ws.Id);
        Assert.Equal(WorkspaceStore.MaxPromptHistory, loaded.Count);
        Assert.Equal($"prompt {WorkspaceStore.MaxPromptHistory + 24}", loaded[0].Prompt); // newest first
        Assert.Equal(clipId, loaded[0].ClipIds.Single());
        Assert.Equal("Walk", loaded[0].ClipName);
        Assert.True(loaded.Zip(loaded.Skip(1)).All(p => p.First.CreatedUtc >= p.Second.CreatedUtc));
    }

    [Fact]
    public void CorruptPromptHistoryStartsFreshInsteadOfFailing()
    {
        var store = new WorkspaceStore(_dir);
        var ws = new AnimationWorkspace { ModelPath = "models/hero.vmdl" };
        store.Save(ws, _rig.Skeleton);
        File.WriteAllText(Path.Combine(_dir, ws.Id.ToString("N"), "prompts.json"), "{ not json");
        var loaded = store.LoadPromptHistory(ws.Id);
        Assert.NotNull(loaded);
        Assert.Empty(loaded);
        store.SavePromptHistory(ws.Id, new[] { new PromptHistoryEntry { Prompt = "jump" }, new PromptHistoryEntry { Prompt = "  " } });
        Assert.Equal(new[] { "jump" }, store.LoadPromptHistory(ws.Id).Select(e => e.Prompt)); // blank prompts are dropped
    }

    /// <summary>A replaced model's workspace is set aside: the path no longer finds it, its files stay.</summary>
    [Fact]
    public void ForgettingAPathKeepsTheOldWorkspaceFiles()
    {
        var store = new WorkspaceStore(_dir);
        var created = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var ws = new AnimationWorkspace { ModelPath = "models/citizen.vmdl", ModelName = "citizen", ModelFileCreatedUtc = created };
        ws.Clips.Add(Fixtures.Walk(_rig));
        store.Save(ws, _rig.Skeleton);
        Assert.Equal(created, store.Load(ws.Id, _rig.Skeleton, new List<string>()).ModelFileCreatedUtc);
        Assert.Equal(ws.Id, store.FindWorkspaceFor("models/citizen.vmdl"));
        store.Forget("Models/Citizen.vmdl");
        Assert.Null(store.FindWorkspaceFor("models/citizen.vmdl"));
        Assert.Single(store.Load(ws.Id, _rig.Skeleton, new List<string>()).Clips);
    }

    [Fact]
    public void ADeletedModelIsForgottenButAReplacingSaveIsNot()
    {
        var gone = new DeletedModels();
        var t = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        // an editor saving by write-then-swap: the file is back within a moment
        gone.Deleted(@"Models\Hero\Hero.vmdl", t);
        gone.Created("models/hero/hero.vmdl", t.AddMilliseconds(50));
        Assert.Empty(gone.Take(t.AddMinutes(1)));
        // deleted for real, then a new model made at the same path minutes later
        gone.Deleted("citizen.vmdl", t);
        Assert.Empty(gone.Take(t.AddSeconds(1))); // not yet: could still be a replacing save
        gone.Created("citizen.vmdl", t.AddMinutes(3));
        Assert.Equal(new[] { "citizen.vmdl" }, gone.Take(t.AddMinutes(3)));
        Assert.Empty(gone.Take(t.AddMinutes(4)));
    }
}
